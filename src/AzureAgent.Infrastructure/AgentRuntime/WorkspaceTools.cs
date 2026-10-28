using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace AzureAgent.Infrastructure.AgentRuntime;

/// <summary>
/// Tools the coding agent uses inside its run directory. Every path is resolved against the root
/// and rejected if it escapes it; commands are limited to the repository policy's allow-list.
/// </summary>
public sealed class WorkspaceTools(string root, IReadOnlySet<string> allowedCommands, IProcessRunner processes, Action<string, string> onToolCall,
    string? homeDirectory = null)
{
    private const int MaxReadChars = 60_000;
    private static readonly string[] IgnoredDirectories = [".git", "node_modules", "bin", "obj", "dist", ".venv", "target"];

    public IList<AITool> Create() =>
    [
        AIFunctionFactory.Create(ListFiles, "list_files"),
        AIFunctionFactory.Create(ReadFile, "read_file"),
        AIFunctionFactory.Create(WriteFile, "write_file"),
        AIFunctionFactory.Create(ReplaceInFile, "replace_in_file"),
        AIFunctionFactory.Create(Search, "search"),
        AIFunctionFactory.Create(RunCommand, "run_command"),
    ];

    public string Resolve(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativePath.TrimStart('/', '\\')));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.Ordinal) && full != rootFull.TrimEnd(Path.DirectorySeparatorChar))
            throw new InvalidOperationException($"Path '{relativePath}' is outside the workspace");
        if (full.Split(Path.DirectorySeparatorChar).Contains(".git")) throw new InvalidOperationException("The .git directory is not accessible");
        return full;
    }

    [Description("List files and directories under a workspace path (relative). Paths start with the repository folder name.")]
    public string ListFiles([Description("Relative directory, e.g. 'api/src'. Empty for the workspace root.")] string path = "",
        [Description("How many directory levels to include (1-4).")] int depth = 2)
    {
        onToolCall("list_files", path);
        var start = Resolve(path);
        if (!Directory.Exists(start)) return $"Not a directory: {path}";
        var builder = new StringBuilder();
        void Walk(string dir, int level)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir).Order())
            {
                var name = Path.GetFileName(entry);
                if (IgnoredDirectories.Contains(name)) continue;
                var isDir = Directory.Exists(entry);
                builder.Append(new string(' ', level * 2)).Append(name).AppendLine(isDir ? "/" : "");
                if (builder.Length > MaxReadChars) return;
                if (isDir && level + 1 < Math.Clamp(depth, 1, 4)) Walk(entry, level + 1);
            }
        }
        Walk(start, 0);
        return builder.Length == 0 ? "(empty)" : builder.ToString();
    }

    [Description("Read a text file. Optionally a 1-based inclusive line range.")]
    public string ReadFile(string path, int? startLine = null, int? endLine = null)
    {
        onToolCall("read_file", path);
        var full = Resolve(path);
        if (!File.Exists(full)) return $"File not found: {path}";
        var lines = File.ReadAllLines(full);
        var from = Math.Max(1, startLine ?? 1);
        var to = Math.Min(lines.Length, endLine ?? lines.Length);
        var text = string.Join('\n', lines.Skip(from - 1).Take(Math.Max(0, to - from + 1)).Select((l, i) => $"{from + i}: {l}"));
        return text.Length > MaxReadChars ? text[..MaxReadChars] + "\n… (truncated; read a smaller range)" : text;
    }

    [Description("Create or overwrite a file with the given content.")]
    public string WriteFile(string path, string content)
    {
        onToolCall("write_file", path);
        var full = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return $"Wrote {content.Length} characters to {path}";
    }

    [Description("Replace an exact, unique snippet in a file. Fails if the snippet is missing or appears more than once.")]
    public string ReplaceInFile(string path, string oldText, string newText)
    {
        onToolCall("replace_in_file", path);
        var full = Resolve(path);
        if (!File.Exists(full)) return $"File not found: {path}";
        var content = File.ReadAllText(full);
        var index = content.IndexOf(oldText, StringComparison.Ordinal);
        if (index < 0) return "oldText was not found; read the file again and copy the snippet exactly";
        if (content.IndexOf(oldText, index + oldText.Length, StringComparison.Ordinal) >= 0) return "oldText appears more than once; include more context";
        File.WriteAllText(full, content[..index] + newText + content[(index + oldText.Length)..]);
        return $"Replaced snippet in {path}";
    }

    [Description("Search file contents with a regular expression. Returns matching lines as path:line: text.")]
    public string Search(string pattern, [Description("Relative directory to search, e.g. 'api'.")] string path = "", int maxResults = 100)
    {
        onToolCall("search", pattern);
        var regex = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        var start = Resolve(path);
        var results = new List<string>();
        foreach (var file in Directory.EnumerateFiles(start, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(IgnoredDirectories.Contains)) continue;
            if (new FileInfo(file).Length > 1_000_000) continue;
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (!regex.IsMatch(line)) continue;
                results.Add($"{relative}:{lineNumber}: {line.Trim()}");
                if (results.Count >= Math.Clamp(maxResults, 1, 500)) return string.Join('\n', results) + "\n… (more results)";
            }
        }
        return results.Count == 0 ? "No matches" : string.Join('\n', results);
    }

    [Description("Run a build/test/lint command inside a repository folder. Only allow-listed executables are permitted; no shell.")]
    public async Task<string> RunCommand([Description("Repository folder (relative), e.g. 'api'.")] string directory,
        [Description("Executable, e.g. 'dotnet' or 'npm'.")] string command, [Description("Arguments, one per item.")] string[] arguments,
        CancellationToken ct = default)
    {
        onToolCall("run_command", $"{command} {string.Join(' ', arguments)}");
        if (!allowedCommands.Contains(command)) return $"Command '{command}' is not allowed. Allowed: {string.Join(", ", allowedCommands.Order())}";
        if (command == "git") return "Use of git is reserved for the platform";
        var environment = homeDirectory is null ? null : new Dictionary<string, string> { ["HOME"] = homeDirectory };
        var result = await processes.RunAsync(command, arguments, Resolve(directory), environment, TimeSpan.FromMinutes(10), ct);
        var tail = result.Output.Length > 20_000 ? "…" + result.Output[^20_000..] : result.Output;
        return result.TimedOut ? $"Timed out after 10 minutes.\n{tail}" : $"Exit code {result.ExitCode}\n{tail}";
    }
}
