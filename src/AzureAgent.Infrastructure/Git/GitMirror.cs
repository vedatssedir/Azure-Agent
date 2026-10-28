using System.Text;
using System.Text.RegularExpressions;
using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Infrastructure.AgentRuntime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureAgent.Infrastructure.Git;

/// <summary>
/// Bare mirrors (<c>git clone --mirror</c>) under <see cref="AgentRuntimeOptions.MirrorRoot"/>. Every
/// read is a plain git command without a shell; references and paths are validated so they can never
/// be read as options, and every command separates paths with <c>--</c>.
/// </summary>
public sealed partial class GitMirror(IProcessRunner processes, IOptions<AgentRuntimeOptions> options, ILogger<GitMirror> logger) : IRepositoryMirror
{
    private const int MaxBlobBytes = 1_048_576;
    private const int MaxPatchBytes = 600_000;
    private const char Field = '\x1f';
    private const char Record = '\x1e';
    private const string LogFormat = "--format=%H%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%s%x1f%B%x1e";
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromMinutes(30);

    private string Root => options.Value.MirrorRoot;

    public string MirrorPath(Guid organizationId, Guid repositoryId) => Path.Combine(Root, organizationId.ToString("N"), repositoryId.ToString("N") + ".git");

    public bool Exists(Guid organizationId, Guid repositoryId) => File.Exists(Path.Combine(MirrorPath(organizationId, repositoryId), "HEAD"));

    private IReadOnlyDictionary<string, string> Environment(string? organizationUrl = null, AzureCredential? credential = null)
    {
        var home = Directory.CreateDirectory(Path.Combine(Root, ".home")).FullName;
        return organizationUrl is not null && credential is not null
            ? GitWorkspace.BuildEnvironment(organizationUrl, credential, home)
            : new Dictionary<string, string> { ["HOME"] = home };
    }

    public async Task SyncAsync(Guid organizationId, Guid repositoryId, string remoteUrl, string organizationUrl, AzureCredential credential, CancellationToken ct)
    {
        var path = MirrorPath(organizationId, repositoryId);
        var env = Environment(organizationUrl, credential);
        if (!Exists(organizationId, repositoryId))
        {
            var parent = Directory.CreateDirectory(Path.GetDirectoryName(path)!).FullName;
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            await GitAsync(parent, env, SyncTimeout, ct, "clone", "--mirror", "--quiet", remoteUrl, path);
            logger.LogInformation("Mirrored repository {RepositoryId}", repositoryId);
            return;
        }
        await GitAsync(path, env, SyncTimeout, ct, "remote", "set-url", "origin", remoteUrl);
        await GitAsync(path, env, SyncTimeout, ct, "fetch", "--prune", "--quiet", "origin");
    }

    private async Task<string> GitAsync(string directory, IReadOnlyDictionary<string, string> env, TimeSpan timeout, CancellationToken ct, params string[] args)
    {
        var result = await processes.RunAsync("git", ["-c", "core.hooksPath=/dev/null", .. args], directory, env, timeout, ct);
        if (result.ExitCode != 0) throw new GitException($"git {args[0]} failed: {(result.Output.Length > 1500 ? result.Output[^1500..] : result.Output).Trim()}");
        return result.Output;
    }

    private async Task<CapturedOutput> ReadAsync(Guid organizationId, Guid repositoryId, int maxBytes, CancellationToken ct, params string[] args)
    {
        var path = MirrorPath(organizationId, repositoryId);
        if (!Exists(organizationId, repositoryId)) throw new NotFoundException("Repository mirror (run a sync first)");
        return await processes.CaptureAsync("git", ["--git-dir", path, "-c", "core.quotePath=false", .. args], path, Environment(), ReadTimeout, maxBytes, ct);
    }

    private async Task<string> ReadTextAsync(Guid organizationId, Guid repositoryId, int maxBytes, CancellationToken ct, params string[] args)
    {
        var output = await ReadAsync(organizationId, repositoryId, maxBytes, ct, args);
        if (output.ExitCode != 0) throw new NotFoundException($"Git object ({output.Stderr.Trim().Split('\n')[0]})");
        return Encoding.UTF8.GetString(output.Stdout);
    }

    /// <summary>Branch, tag or commit; never something git could read as an option or a range.</summary>
    public static string Reference(string reference)
    {
        var value = reference.Trim();
        if (value.Length is 0 or > 250 || value.StartsWith('-') || value.Contains("..") || !ReferencePattern().IsMatch(value))
            throw new BusinessRuleException("Invalid branch or commit");
        return value;
    }

    /// <summary>Repository-relative path without traversal.</summary>
    public static string RelativePath(string? path)
    {
        var value = (path ?? "").Replace('\\', '/').Trim('/');
        if (value.Split('/').Any(part => part is ".." or ".") || value.StartsWith('-') || value.Contains('\0') || value.Contains(':'))
            throw new BusinessRuleException("Invalid path");
        return value;
    }

    public async Task<IReadOnlyList<GitBranch>> BranchesAsync(Guid organizationId, Guid repositoryId, CancellationToken ct)
    {
        var text = await ReadTextAsync(organizationId, repositoryId, 4_000_000, ct, "for-each-ref", "--sort=-committerdate",
            "--format=%(refname:short)%1f%(objectname)%1f%(committerdate:iso-strict)%1f%(subject)%1e", "refs/heads");
        return text.Split(Record, StringSplitOptions.RemoveEmptyEntries).Select(r => r.Trim('\n').Split(Field)).Where(f => f.Length >= 4)
            .Select(f => new GitBranch(f[0], f[1], DateTimeOffset.Parse(f[2]), f[3])).ToList();
    }

    public async Task<IReadOnlyList<GitTreeEntry>> TreeAsync(Guid organizationId, Guid repositoryId, string reference, string path, CancellationToken ct)
    {
        var relative = RelativePath(path);
        string[] args = relative.Length == 0
            ? ["ls-tree", "-z", "--long", Reference(reference)]
            : ["ls-tree", "-z", "--long", Reference(reference), "--", relative + "/"];
        var text = await ReadTextAsync(organizationId, repositoryId, 8_000_000, ct, args);
        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(line =>
            {
                var tab = line.IndexOf('\t');
                var meta = line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var fullPath = line[(tab + 1)..];
                return new GitTreeEntry(fullPath.Split('/')[^1], fullPath, meta[1] == "tree", long.TryParse(meta[3], out var size) ? size : 0);
            })
            .Where(e => e.Name.Length > 0)
            .OrderBy(e => !e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<GitBlob?> BlobAsync(Guid organizationId, Guid repositoryId, string reference, string path, CancellationToken ct)
    {
        var spec = $"{Reference(reference)}:{RelativePath(path)}";
        var sizeOutput = await ReadAsync(organizationId, repositoryId, 1024, ct, "cat-file", "-s", spec);
        if (sizeOutput.ExitCode != 0) return null;
        var size = long.Parse(Encoding.ASCII.GetString(sizeOutput.Stdout).Trim());
        var content = await ReadAsync(organizationId, repositoryId, MaxBlobBytes, ct, "cat-file", "blob", spec);
        var bytes = content.Stdout;
        var binary = bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).IndexOf((byte)0) >= 0;
        return new GitBlob(RelativePath(path), size, binary, content.Truncated, binary ? null : Encoding.UTF8.GetString(bytes));
    }

    private static List<GitCommitInfo> ParseLog(string text) =>
        text.Split(Record, StringSplitOptions.RemoveEmptyEntries).Select(r => r.TrimStart('\n').Split(Field)).Where(f => f.Length >= 7)
            .Select(f => new GitCommitInfo(f[0], f[2], f[3], DateTimeOffset.Parse(f[4]), f[5], f[6].Trim(),
                f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries))).ToList();

    public async Task<IReadOnlyList<GitCommitInfo>> LogAsync(Guid organizationId, Guid repositoryId, string reference, string? path, int skip, int take,
        CancellationToken ct)
    {
        var relative = RelativePath(path);
        List<string> args = ["log", LogFormat, $"--skip={Math.Max(0, skip)}", $"--max-count={Math.Clamp(take, 1, 200)}", Reference(reference)];
        if (relative.Length > 0) args.AddRange(["--", relative]);
        return ParseLog(await ReadTextAsync(organizationId, repositoryId, 8_000_000, ct, [.. args]));
    }

    public async Task<IReadOnlyList<GitCommitInfo>> CommitsSinceAsync(Guid organizationId, Guid repositoryId, DateTimeOffset since, CancellationToken ct) =>
        Exists(organizationId, repositoryId)
            ? ParseLog(await ReadTextAsync(organizationId, repositoryId, 16_000_000, ct, "log", "--branches", LogFormat, "--max-count=2000",
                $"--since={since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}"))
            : [];

    public async Task<GitCommitDetail?> CommitAsync(Guid organizationId, Guid repositoryId, string commitId, CancellationToken ct)
    {
        if (!CommitIdPattern().IsMatch(commitId)) throw new BusinessRuleException("Invalid commit id");
        var header = await ReadAsync(organizationId, repositoryId, 1_000_000, ct, "show", "-s", LogFormat, commitId);
        if (header.ExitCode != 0) return null;
        var commit = ParseLog(Encoding.UTF8.GetString(header.Stdout)).SingleOrDefault();
        if (commit is null) return null;
        var (files, truncated) = await ChangesAsync(organizationId, repositoryId, ["show", "--format=", "--diff-merges=first-parent", "-M", commitId], ct);
        return new GitCommitDetail(commit, files, truncated);
    }

    public async Task<IReadOnlyList<GitFileChange>> CompareAsync(Guid organizationId, Guid repositoryId, string target, string source, CancellationToken ct) =>
        (await ChangesAsync(organizationId, repositoryId, ["diff", "-M", $"{Reference(target)}...{Reference(source)}"], ct)).Files;

    private async Task<(IReadOnlyList<GitFileChange> Files, bool Truncated)> ChangesAsync(Guid organizationId, Guid repositoryId, string[] diff, CancellationToken ct)
    {
        var names = (await ReadTextAsync(organizationId, repositoryId, 4_000_000, ct, [.. diff, "--name-status", "-z"])).Split('\0');
        var numstat = (await ReadTextAsync(organizationId, repositoryId, 4_000_000, ct, [.. diff, "--numstat"]))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).ToList();
        var patch = await ReadAsync(organizationId, repositoryId, MaxPatchBytes, ct, [.. diff, "--patch", "--no-color", "--no-ext-diff"]);
        var patches = SplitPatch(Encoding.UTF8.GetString(patch.Stdout));

        var files = new List<GitFileChange>();
        for (int i = 0, n = 0; i < names.Length && names[i].Length > 0; n++)
        {
            var status = names[i];
            string? oldPath = null;
            string path;
            if (status.StartsWith('R') || status.StartsWith('C')) { oldPath = names[i + 1]; path = names[i + 2]; i += 3; }
            else { path = names[i + 1]; i += 2; }
            var stat = n < numstat.Count ? numstat[n] : ["0", "0"];
            var binary = stat[0] == "-";
            files.Add(new GitFileChange(path, oldPath, status[..1] switch
                {
                    "A" => "add", "D" => "delete", "R" => "rename", "C" => "copy", _ => "edit",
                }, binary ? 0 : int.Parse(stat[0]), binary ? 0 : int.Parse(stat[1]), patches.GetValueOrDefault(path) ?? "", binary));
        }
        return (files, patch.Truncated);
    }

    /// <summary>Splits a unified diff into per-file patches keyed by the new path.</summary>
    private static Dictionary<string, string> SplitPatch(string text)
    {
        var result = new Dictionary<string, string>();
        foreach (var part in text.Split("\ndiff --git ").Select((p, i) => i == 0 ? p : "diff --git " + p))
        {
            if (!part.StartsWith("diff --git ", StringComparison.Ordinal)) continue;
            var header = DiffHeader().Match(part.Split('\n')[0]);
            var plus = part.Split('\n').FirstOrDefault(l => l.StartsWith("+++ b/", StringComparison.Ordinal))?[6..]
                ?? (header.Success ? header.Groups[1].Value : null);
            if (plus is not null) result[plus] = part.TrimEnd('\n');
        }
        return result;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._/@^~{}+-]+$")]
    private static partial Regex ReferencePattern();

    [GeneratedRegex("^[0-9a-f]{7,64}$")]
    private static partial Regex CommitIdPattern();

    [GeneratedRegex(@"^diff --git a/.+ b/(.+)$")]
    private static partial Regex DiffHeader();
}
