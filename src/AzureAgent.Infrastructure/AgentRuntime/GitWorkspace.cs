using AzureAgent.Application.Abstractions;

namespace AzureAgent.Infrastructure.AgentRuntime;

public sealed class GitException(string message) : Exception(message);

/// <summary>
/// Git operations for one run. The credential (PAT or Entra ID token) travels as an http.extraHeader
/// in environment config (GIT_CONFIG_*), scoped to the Azure DevOps origin; it never appears in argv or remote URLs.
/// </summary>
public sealed class GitWorkspace(IProcessRunner processes, string organizationUrl, AzureCredential credential, string authorName, string authorEmail)
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(10);

    public IReadOnlyDictionary<string, string> Environment { get; } = BuildEnvironment(organizationUrl, credential);

    public static IReadOnlyDictionary<string, string> BuildEnvironment(string organizationUrl, AzureCredential credential)
    {
        var origin = new Uri(organizationUrl).GetLeftPart(UriPartial.Authority);
        var header = $"Authorization: {credential.HeaderValue}";
        return new Dictionary<string, string>
        {
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = $"http.{origin}/.extraHeader",
            ["GIT_CONFIG_VALUE_0"] = header,
        };
    }

    public async Task<string> RunAsync(string directory, CancellationToken ct, params string[] args)
    {
        string[] identity = ["-c", $"user.name={authorName}", "-c", $"user.email={authorEmail}", "-c", "core.hooksPath=/dev/null"];
        var result = await processes.RunAsync("git", [.. identity, .. args], directory, Environment, GitTimeout, ct);
        if (result.ExitCode != 0) throw new GitException($"git {args[0]} failed: {Truncate(result.Output)}");
        return result.Output.Trim();
    }

    public async Task CloneAsync(string parent, string remoteUrl, string name, string branch, CancellationToken ct)
    {
        Directory.CreateDirectory(parent);
        await RunAsync(parent, ct, "clone", "--depth", "50", "--branch", branch, "--single-branch", remoteUrl, name);
    }

    public async Task<IReadOnlyList<string>> ChangedFilesAsync(string directory, CancellationToken ct)
    {
        var status = await RunAsync(directory, ct, "status", "--porcelain");
        return status.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Length > 3 ? l[3..].Trim() : l).ToList();
    }

    private static string Truncate(string text) => text.Length > 2000 ? text[^2000..] : text;
}
