using System.Text.Json.Nodes;
using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Features.Auth;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Application.Features.Projects;
using AzureAgent.Application.Features.Runs;
using AzureAgent.Domain.Agents;
using AzureAgent.IntegrationTests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace AzureAgent.IntegrationTests;

/// <summary>All API test classes share one host, database and Azure organization, so they run as one collection.</summary>
[CollectionDefinition(Name)]
public sealed class PlatformCollection : ICollectionFixture<AppFactory>
{
    public const string Name = "platform";
}

public abstract class PlatformTestBase(AppFactory factory)
{
    protected const string Password = "Sup3rSecretPass";
    protected const string OrganizationUrl = "https://dev.azure.com/contoso";
    protected const string AdminPat = "adminPersonalAccessToken0000001";
    /// <summary>The project that exists in Azure before setup, so setup imports it.</summary>
    protected const string ShopProject = "Shop";
    private static readonly SemaphoreSlim BootstrapLock = new(1, 1);
    private static (string Token, Guid OrganizationId)? _admin;

    protected AppFactory Factory { get; } = factory;
    protected FakeAzureDevOpsFactory Azure => Factory.Azure;

    protected static string AdminToken => _admin!.Value.Token;

    protected Api NewApi() => new(Factory.CreateClient());

    /// <summary>
    /// First-run setup, once for the whole run: the administrator gives the organization URL and a PAT,
    /// and the organization (with the Shop project, its board and repository history) is imported.
    /// </summary>
    protected async Task<(Api Api, Guid OrganizationId)> AdminAsync()
    {
        await BootstrapLock.WaitAsync();
        try
        {
            if (_admin is null)
            {
                Azure.TokenIdentities[AdminPat] = new AzureIdentity("Admin", "admin@contoso.com");
                var shop = Azure.Project(ShopProject);
                Factory.CreateRemoteRepository(shop, "api");
                Factory.Commit(shop, "api", "AB#101 Add paging", ("src/Paging.cs", "public static class Paging { }\n"));
                shop.AddWorkItem(101, "Add paging", "To Do", tags: "repo:api");
                shop.AddWorkItem(102, "Old bug", "Done", assignee: "jane@contoso.com", tags: "repo:api");

                var setup = await NewApi().Post<AuthResult>("/api/auth/setup", new
                {
                    email = "admin@contoso.com", displayName = "Admin", password = Password, azureOrganizationUrl = OrganizationUrl, personalAccessToken = AdminPat,
                });
                var api = NewApi();
                api.Authenticate(setup.AccessToken);
                var me = await api.Get<MeDto>("/api/auth/me");
                _admin = (setup.AccessToken, me.Organizations.Single().OrganizationId);
                api.Authenticate(setup.AccessToken, _admin.Value.OrganizationId);
                await Eventually(async () => (await api.Get<List<ProjectDto>>("/api/projects")).Any(p => p.Name == ShopProject && p.LastSyncedAt != null), "setup import");
                (await api.Send(HttpMethod.Put, "/api/settings/ai/credentials/ANTHROPIC_API_KEY", new { value = "sk-ant-test" })).EnsureSuccessStatusCode();
            }
        }
        finally { BootstrapLock.Release(); }
        var result = NewApi();
        result.Authenticate(_admin.Value.Token, _admin.Value.OrganizationId);
        return (result, _admin.Value.OrganizationId);
    }

    protected async Task<ProjectDto> ShopAsync(Api admin) => (await admin.Get<List<ProjectDto>>("/api/projects")).Single(p => p.Name == ShopProject);

    /// <summary>Runs one project reconciliation in-process and returns what it did (queued runs cascade as usual).</summary>
    protected async Task<SyncResult> SyncAsync(Guid organizationId, Guid projectId)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<SyncResult>(new SyncProject(organizationId, projectId, "test"));
    }

    /// <summary>Creates a project in the fake Azure organization, lets the organization sync discover it and returns it fully synced.</summary>
    protected async Task<ProjectDto> NewProjectAsync(Api admin, Guid organizationId, string name, Action<FakeAzureDevOps>? configure = null)
    {
        var azure = Azure.Project(name);
        configure?.Invoke(azure);
        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<OrganizationSyncResult>(new SyncOrganization(organizationId, "test"));
        var project = (await admin.Get<List<ProjectDto>>("/api/projects")).Single(p => p.Name == name);
        await SyncAsync(organizationId, project.Id);
        return await admin.Get<ProjectDto>($"/api/projects/{project.Id}");
    }

    /// <summary>A developer self sign-up with a PAT that Azure says belongs to them.</summary>
    protected async Task<Api> SignUpAsync(string email, string name, Guid organizationId, string? pat = null)
    {
        pat ??= $"pat{new string(email.Where(char.IsLetterOrDigit).ToArray())}000000000000000000"[..30];
        Azure.TokenIdentities[pat] = new AzureIdentity(name, email);
        var auth = await NewApi().Post<AuthResult>("/api/auth/signup", new { email, displayName = name, password = Password, personalAccessToken = pat });
        var api = NewApi();
        api.Authenticate(auth.AccessToken, organizationId);
        return api;
    }

    /// <summary>The developer's session: signs them up the first time (tests share one database).</summary>
    protected async Task<Api> DeveloperAsync(string email, string name, Guid organizationId)
    {
        try { return await LoginAsync(email, organizationId); }
        catch (HttpRequestException) { return await SignUpAsync(email, name, organizationId); }
    }

    protected async Task<Api> LoginAsync(string email, Guid organizationId, string password = Password)
    {
        var auth = await NewApi().Post<AuthResult>("/api/auth/login", new { email, password });
        var api = NewApi();
        api.Authenticate(auth.AccessToken, organizationId);
        return api;
    }

    /// <summary>Changes organization settings (automation, people, AI defaults) the way the Settings page does.</summary>
    protected static async Task UpdateSettingsAsync(Api admin, object changes)
    {
        var current = await admin.Get<JsonObject>("/api/settings");
        var settings = current["automation"]!.AsObject();
        foreach (var (key, value) in System.Text.Json.JsonSerializer.SerializeToNode(changes, AppFactory.Json)!.AsObject()) settings[key] = value?.DeepClone();
        (await admin.Send(HttpMethod.Put, "/api/settings/automation", new { settings, monthlyBudgetUsd = current["monthlyBudgetUsd"]?.DeepClone() })).EnsureSuccessStatusCode();
    }

    protected static async Task<RunDetailDto> WaitForRunAsync(Api api, Func<RunSummaryDto, bool> match)
    {
        for (var i = 0; i < 200; i++)
        {
            var run = (await api.Get<List<RunSummaryDto>>("/api/runs")).FirstOrDefault(match);
            if (run is { Status: RunStatus.Succeeded or RunStatus.Failed or RunStatus.Cancelled }) return await api.Get<RunDetailDto>($"/api/runs/{run.Id}");
            await Task.Delay(200);
        }
        throw new TimeoutException("Run did not finish");
    }

    protected static async Task Eventually(Func<Task<bool>> condition, string what)
    {
        for (var i = 0; i < 150; i++)
        {
            if (await condition()) return;
            await Task.Delay(200);
        }
        throw new TimeoutException($"Timed out waiting for {what}");
    }
}
