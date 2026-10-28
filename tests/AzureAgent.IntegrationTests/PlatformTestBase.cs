using AzureAgent.Application.Features.Auth;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Application.Features.Organizations;
using AzureAgent.Application.Features.Projects;
using AzureAgent.Application.Features.Runs;
using AzureAgent.Domain.Agents;
using AzureAgent.Domain.Identity;

namespace AzureAgent.IntegrationTests;

/// <summary>All API test classes share one host and database, so they run as one collection.</summary>
[CollectionDefinition(Name)]
public sealed class PlatformCollection : ICollectionFixture<AppFactory>
{
    public const string Name = "platform";
}

public abstract class PlatformTestBase(AppFactory factory)
{
    protected const string Password = "Sup3rSecretPass";
    private static readonly SemaphoreSlim BootstrapLock = new(1, 1);
    private static (string Token, Guid OrganizationId)? _admin;

    protected AppFactory Factory { get; } = factory;

    protected static string AdminToken => _admin!.Value.Token;

    protected Api NewApi() => new(Factory.CreateClient());

    protected async Task<(Api Api, Guid OrganizationId)> AdminAsync()
    {
        await BootstrapLock.WaitAsync();
        try
        {
            if (_admin is null)
            {
                var setup = await NewApi().Post<AuthResult>("/api/auth/setup",
                    new { email = "admin@contoso.com", displayName = "Admin", password = Password, organizationName = "Contoso" });
                var api = NewApi();
                api.Authenticate(setup.AccessToken);
                var me = await api.Get<MeDto>("/api/auth/me");
                _admin = (setup.AccessToken, me.Organizations.Single().OrganizationId);
            }
        }
        finally { BootstrapLock.Release(); }
        var result = NewApi();
        result.Authenticate(_admin.Value.Token, _admin.Value.OrganizationId);
        return (result, _admin.Value.OrganizationId);
    }

    protected async Task<Api> LoginAsync(string email, Guid organizationId, string password = Password)
    {
        var auth = await NewApi().Post<AuthResult>("/api/auth/login", new { email, password });
        var api = NewApi();
        api.Authenticate(auth.AccessToken, organizationId);
        return api;
    }

    protected async Task<ProjectDto> CreateProjectAsync(Api admin, string key, bool servicePat = true)
    {
        var project = await admin.Post<ProjectDto>("/api/projects", new
        {
            name = $"Shop {key}", key, azureOrganizationUrl = "https://dev.azure.com/contoso", azureProjectName = key,
        });
        if (servicePat) (await admin.Send(HttpMethod.Put, $"/api/projects/{project.Id}/secrets/AZURE_DEVOPS_PAT", new { value = "pat-123456" })).EnsureSuccessStatusCode();
        (await admin.Send(HttpMethod.Put, $"/api/projects/{project.Id}/secrets/ANTHROPIC_API_KEY", new { value = "sk-ant-test" })).EnsureSuccessStatusCode();
        return project;
    }

    protected async Task<OrganizationMemberDto> AddUserAsync(Api admin, string email, string name, OrganizationRole role = OrganizationRole.Member)
    {
        try
        {
            return await admin.Post<OrganizationMemberDto>("/api/organizations/members", new { email, displayName = name, role, initialPassword = Password });
        }
        catch (HttpRequestException)
        {
            return (await admin.Get<List<OrganizationMemberDto>>("/api/organizations/members")).Single(m => m.Email == email);
        }
    }

    protected static async Task<RunDetailDto> WaitForRunAsync(Api api, Guid projectId, Func<RunSummaryDto, bool> match)
    {
        for (var i = 0; i < 150; i++)
        {
            var run = (await api.Get<List<RunSummaryDto>>($"/api/projects/{projectId}/runs")).FirstOrDefault(match);
            if (run is { Status: RunStatus.Succeeded or RunStatus.Failed or RunStatus.Cancelled })
                return await api.Get<RunDetailDto>($"/api/projects/{projectId}/runs/{run.Id}");
            await Task.Delay(200);
        }
        throw new TimeoutException("Run did not finish");
    }
}
