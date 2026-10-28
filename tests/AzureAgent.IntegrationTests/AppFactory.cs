using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureAgent.Application.Abstractions;
using AzureAgent.Infrastructure.AgentRuntime;
using AzureAgent.IntegrationTests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace AzureAgent.IntegrationTests;

/// <summary>
/// Hosts the real API against a real PostgreSQL database (TEST_POSTGRES, default local) with
/// Azure DevOps and the model provider replaced by in-memory fakes. RabbitMQ/Redis are disabled,
/// so Wolverine uses local queues and the cache is in-memory.
/// </summary>
public sealed class AppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static readonly string ConnectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES")
        ?? "Host=localhost;Database=azure_agent_test;Username=azure_agent;Password=azure_agent";

    public FakeAzureDevOpsFactory Azure { get; } = new();
    public ScriptedChatClients Models { get; } = new();
    public FakeIdentityProvider Sso { get; } = new();
    public FakeTokenProvider Tokens { get; } = new();
    public string Root { get; } = Directory.CreateTempSubdirectory("azure-agent-tests-").FullName;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", ConnectionString);
        builder.UseSetting("ConnectionStrings:Redis", "");
        builder.UseSetting("ConnectionStrings:RabbitMq", "");
        builder.UseSetting("Jwt:SigningKey", "integration-tests-signing-key-0123456789abcdef");
        builder.UseSetting("Encryption:Key", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("Jobs:Enabled", "false");
        builder.UseSetting("AgentRuntime:WorkspaceRoot", Path.Combine(Root, "workspaces"));
        builder.UseSetting("Messaging:AgentWorkers", "2");
        builder.UseSetting("App:PublicUrl", "https://agent.test");
        // Every test client shares one IP, so the per-IP sign-in limit is raised here.
        builder.UseSetting("RateLimits:AuthPerMinute", "10000");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAzureDevOpsClientFactory>();
            services.AddSingleton<IAzureDevOpsClientFactory>(Azure);
            services.RemoveAll<IChatClientFactory>();
            services.AddSingleton<IChatClientFactory>(Models);
            services.RemoveAll<IExternalIdentityProvider>();
            services.AddSingleton<IExternalIdentityProvider>(Sso);
            services.RemoveAll<IAzureTokenProvider>();
            services.AddSingleton<IAzureTokenProvider>(Tokens);
        });
    }

    public async ValueTask InitializeAsync()
    {
        // Program ends with RunJasperFxCommands; this makes it start the host under the test server.
        JasperFx.CommandLine.JasperFxEnvironment.AutoStartHost = true;
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("DROP SCHEMA IF EXISTS app CASCADE; DROP SCHEMA IF EXISTS wolverine CASCADE;", connection);
        await command.ExecuteNonQueryAsync();
        _ = Server; // start the host (runs migrations)
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }

    /// <summary>Creates a bare repository with one commit on main and registers it in the fake Azure project.</summary>
    public string CreateRemoteRepository(FakeAzureDevOps azure, string name)
    {
        var bare = Path.Combine(Root, "remotes", $"{name}.git");
        var seed = Path.Combine(Root, "seed", name);
        Git(Root, "init", "--bare", "-b", "main", bare);
        Git(Root, "clone", bare, seed);
        File.WriteAllText(Path.Combine(seed, "README.md"), $"# {name}\n");
        Git(seed, "add", "-A");
        Git(seed, "commit", "-m", "init");
        Git(seed, "push", "origin", "main");
        azure.Repositories.Add(new AzureRepository($"repo-{name}", name, "main", bare, $"https://dev.azure.com/contoso/Shop/_git/{name}"));
        return bare;
    }

    public static string Git(string cwd, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-c", "user.name=test", "-c", "user.email=test@test" }.Concat(args)) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {output}");
        return output;
    }
}

/// <summary>Authenticated API client bound to one organization.</summary>
public sealed class Api(HttpClient http)
{
    public HttpClient Http { get; } = http;

    public void Authenticate(string token, Guid? organizationId = null)
    {
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Http.DefaultRequestHeaders.Remove("X-Organization-Id");
        if (organizationId is { } id) Http.DefaultRequestHeaders.Add("X-Organization-Id", id.ToString());
    }

    public async Task<T> Get<T>(string url) => await Read<T>(await Http.GetAsync(url));
    public async Task<T> Post<T>(string url, object body) => await Read<T>(await Http.PostAsJsonAsync(url, body, AppFactory.Json));
    public async Task<T> Put<T>(string url, object body) => await Read<T>(await Http.PutAsJsonAsync(url, body, AppFactory.Json));
    public Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null) =>
        Http.SendAsync(new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body, options: AppFactory.Json) });

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{(int)response.StatusCode}: {text}", null, response.StatusCode);
        return JsonSerializer.Deserialize<T>(text, AppFactory.Json)!;
    }
}
