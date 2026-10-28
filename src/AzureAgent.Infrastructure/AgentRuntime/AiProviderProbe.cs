using System.Net.Http.Headers;
using AzureAgent.Application.Abstractions;
using AzureAgent.Domain.Projects;

namespace AzureAgent.Infrastructure.AgentRuntime;

/// <summary>
/// Verifies provider credentials: API keys with the provider's model list (no tokens spent), CLIs
/// with a one-turn prompt in an empty, private workspace.
/// </summary>
public sealed class AiProviderProbe(IHttpClientFactory http, IProcessRunner processes, ICodingCliFactory clis)
    : IAiProviderProbe
{
    public const string HttpClientName = "ai-probe";

    public async Task<(bool Ok, string Detail)> ProbeAsync(string provider, IReadOnlyDictionary<string, string> credentials, string? endpoint, CancellationToken ct)
    {
        try
        {
            return provider switch
            {
                "anthropic" => await GetAsync("https://api.anthropic.com/v1/models?limit=5", r =>
                {
                    r.Headers.Add("x-api-key", credentials[SecretNames.AnthropicApiKey]);
                    r.Headers.Add("anthropic-version", "2023-06-01");
                }, ct),
                "openai" => await GetAsync("https://api.openai.com/v1/models", r =>
                    r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials[SecretNames.OpenAiApiKey]), ct),
                "gemini" => await GetAsync("https://generativelanguage.googleapis.com/v1beta/models?pageSize=5", r =>
                    r.Headers.Add("x-goog-api-key", credentials[SecretNames.GeminiApiKey]), ct),
                "azure-openai" => endpoint is null
                    ? (false, "Set the Azure OpenAI endpoint first")
                    : await GetAsync($"{endpoint.TrimEnd('/')}/openai/models?api-version=2024-10-21", r =>
                        r.Headers.Add("api-key", credentials[SecretNames.AzureOpenAiApiKey]), ct),
                "claude-code" => await CliAsync(Domain.Agents.AgentRuntime.ClaudeCode, "claude-haiku-4-5", ClaudeCredentials(credentials), ct),
                "cursor" => await CliAsync(Domain.Agents.AgentRuntime.CursorCli, "auto",
                    new Dictionary<string, string> { ["CURSOR_API_KEY"] = credentials[SecretNames.CursorApiKey] }, ct),
                _ => (false, "Unknown provider"),
            };
        }
        catch (KeyNotFoundException)
        {
            return (false, "No credential is configured");
        }
        catch (HttpRequestException e)
        {
            return (false, $"Could not reach the provider: {e.Message}");
        }
    }

    private static Dictionary<string, string> ClaudeCredentials(IReadOnlyDictionary<string, string> credentials) =>
        credentials.TryGetValue(SecretNames.ClaudeCodeOAuthToken, out var token)
            ? new() { ["CLAUDE_CODE_OAUTH_TOKEN"] = token }
            : new() { ["ANTHROPIC_API_KEY"] = credentials[SecretNames.AnthropicApiKey] };

    private async Task<(bool, string)> GetAsync(string url, Action<HttpRequestMessage> authorize, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        authorize(request);
        using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
        return response.IsSuccessStatusCode
            ? (true, "Connected")
            : (false, response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? "The provider rejected the key"
                : $"The provider answered HTTP {(int)response.StatusCode}");
    }

    private async Task<(bool, string)> CliAsync(Domain.Agents.AgentRuntime runtime, string model, Dictionary<string, string> credentials, CancellationToken ct)
    {
        var root = Directory.CreateTempSubdirectory("ai-probe-").FullName;
        try
        {
            var cli = clis.Create(runtime);
            var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
            var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
            var (arguments, environment) = cli.Prepare(new CliInvocation(model, work, home, "Answer in one word.", "Reply with OK.", [], true, 1, 0.05m, credentials));
            CliResult? result = null;
            var exit = await processes.RunStreamingAsync(cli.Executable, arguments, work, environment, TimeSpan.FromMinutes(2), line =>
            {
                foreach (var e in cli.Parse(line)) if (e is CliResult r) result = r;
                return Task.CompletedTask;
            }, ct);
            if (result is null) return (false, $"{cli.DisplayName} exited with {exit.ExitCode}: {exit.Output.Trim().Split('\n').LastOrDefault()}");
            return result.IsError ? (false, result.Text) : (true, $"{cli.DisplayName} answered: {result.Text.Trim()}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, $"{runtime} is not installed on this server");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
