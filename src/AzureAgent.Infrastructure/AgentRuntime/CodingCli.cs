using System.Text.Json;
using System.Text.Json.Nodes;
using AzureAgent.Domain.Agents;

namespace AzureAgent.Infrastructure.AgentRuntime;

// The enum shares its name with this namespace.
using AgentRuntime = AzureAgent.Domain.Agents.AgentRuntime;

/// <summary>An MCP server handed to a CLI agent through its config file (secrets resolved).</summary>
public sealed record CliMcpServer(string Name, McpTransport Transport, string? Command, IReadOnlyList<string> Arguments, string? Url,
    IReadOnlyDictionary<string, string> Secrets);

/// <summary>Everything one CLI agent run needs. The CLI never receives Azure credentials: git stays with the platform.</summary>
public sealed record CliInvocation(
    string Model,
    string WorkingDirectory,
    string HomeDirectory,
    string Instructions,
    string Prompt,
    IReadOnlyCollection<string> AllowedCommands,
    bool ReadOnly,
    int MaxTurns,
    decimal MaxBudgetUsd,
    IReadOnlyDictionary<string, string> Credentials,
    IReadOnlyList<CliMcpServer> McpServers);

public abstract record CliEvent;

public sealed record CliText(string Text) : CliEvent;

public sealed record CliToolUse(string Tool, string Detail) : CliEvent;

/// <summary>Usage of one model request. Input includes cached input (as the platform's price model expects).</summary>
public sealed record CliModelCall(long InputTokens, long OutputTokens, long CachedInputTokens) : CliEvent;

/// <summary>Final event. Totals are null when the CLI does not report them.</summary>
public sealed record CliResult(string Text, bool IsError, decimal? CostUsd, long? InputTokens, long? OutputTokens, long? CachedInputTokens, int? Turns) : CliEvent;

/// <summary>Builds the command line for one coding CLI and parses its stream-json output. One instance per run (parsers keep state).</summary>
public interface ICodingCliAdapter
{
    AgentRuntime Runtime { get; }
    string DisplayName { get; }
    string Executable { get; }
    /// <summary>Writes the run's config files (permissions, MCP) and returns arguments and environment.</summary>
    (IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment) Prepare(CliInvocation invocation);
    IEnumerable<CliEvent> Parse(string line);
}

public interface ICodingCliFactory
{
    ICodingCliAdapter Create(AgentRuntime runtime);
}

public sealed class CodingCliFactory(Microsoft.Extensions.Options.IOptions<AgentRuntimeOptions> options) : ICodingCliFactory
{
    public ICodingCliAdapter Create(AgentRuntime runtime) => runtime switch
    {
        AgentRuntime.ClaudeCode => new ClaudeCodeAdapter(options.Value.ClaudeCliPath),
        AgentRuntime.CursorCli => new CursorCliAdapter(options.Value.CursorCliPath),
        _ => throw new ArgumentOutOfRangeException(nameof(runtime), runtime, "Not a CLI runtime"),
    };
}

internal static class CliJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static JsonNode? TryParse(string line)
    {
        if (!line.StartsWith('{')) return null;
        try { return JsonNode.Parse(line); }
        catch (JsonException) { return null; }
    }

    public static long Long(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;

    public static string Summarize(JsonNode? node, int max = 400)
    {
        var text = node?.ToJsonString() ?? "";
        return text.Length > max ? text[..max] + "…" : text;
    }

    /// <summary>The shared mcpServers document both CLIs read (Claude: --mcp-config, Cursor: .cursor/mcp.json).</summary>
    public static JsonObject McpDocument(IReadOnlyList<CliMcpServer> servers)
    {
        var map = new JsonObject();
        foreach (var server in servers)
        {
            map[server.Name] = server.Transport == McpTransport.Stdio
                ? new JsonObject
                {
                    ["command"] = server.Command,
                    ["args"] = new JsonArray(server.Arguments.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
                    ["env"] = new JsonObject(server.Secrets.Select(s => KeyValuePair.Create(s.Key, (JsonNode?)JsonValue.Create(s.Value)))),
                }
                : new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = server.Url,
                    ["headers"] = new JsonObject(server.Secrets.Select(s => KeyValuePair.Create(s.Key, (JsonNode?)JsonValue.Create(s.Value)))),
                };
        }
        return new JsonObject { ["mcpServers"] = map };
    }

    public static string WriteFile(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }
}

/// <summary>
/// Claude Code (<c>claude -p</c>). Permission mode <c>dontAsk</c> denies every tool that is not
/// pre-approved, so the repository's allowed commands become <c>Bash(cmd:*)</c> rules and nothing else
/// runs. <c>--bare</c> skips hooks and plugins a repository could bring; settings and sessions live in
/// the run's private HOME.
/// </summary>
public sealed class ClaudeCodeAdapter(string executable) : ICodingCliAdapter
{
    private static readonly string[] ReadTools = ["Read", "Glob", "Grep", "LS"];
    private static readonly string[] WriteTools = ["Write", "Edit", "MultiEdit", "NotebookEdit", "TodoWrite"];
    private readonly HashSet<string> _messages = [];

    public AgentRuntime Runtime => AgentRuntime.ClaudeCode;
    public string DisplayName => "Claude Code CLI";
    public string Executable => executable;

    public (IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment) Prepare(CliInvocation invocation)
    {
        var allowed = new List<string>(ReadTools);
        if (!invocation.ReadOnly)
        {
            allowed.AddRange(WriteTools);
            allowed.AddRange(invocation.AllowedCommands.Where(c => c != "git").Select(c => $"Bash({c}:*)"));
        }
        allowed.AddRange(invocation.McpServers.Select(s => $"mcp__{s.Name}"));
        var instructions = CliJson.WriteFile(invocation.HomeDirectory, "instructions.md", invocation.Instructions);
        var arguments = new List<string>
        {
            "-p", invocation.Prompt,
            "--output-format", "stream-json", "--verbose",
            "--model", invocation.Model,
            "--permission-mode", "dontAsk",
            "--bare", "--no-session-persistence", "--strict-mcp-config",
            "--max-turns", invocation.MaxTurns.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--max-budget-usd", invocation.MaxBudgetUsd.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--append-system-prompt-file", instructions,
        };
        if (invocation.McpServers.Count > 0)
            arguments.AddRange(["--mcp-config", CliJson.WriteFile(invocation.HomeDirectory, "mcp.json", CliJson.McpDocument(invocation.McpServers).ToJsonString(CliJson.Options))]);
        arguments.AddRange(["--disallowedTools", "Bash(git:*)", "WebFetch", "WebSearch"]);
        arguments.Add("--allowedTools");
        arguments.AddRange(allowed);

        var environment = new Dictionary<string, string>(invocation.Credentials)
        {
            ["HOME"] = invocation.HomeDirectory,
            ["DISABLE_AUTOUPDATER"] = "1",
            ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
        };
        return (arguments, environment);
    }

    public IEnumerable<CliEvent> Parse(string line)
    {
        var node = CliJson.TryParse(line);
        switch (node?["type"]?.GetValue<string>())
        {
            case "assistant":
                var message = node["message"];
                // One API response can arrive as several events (one per content block) carrying the same usage.
                if (message?["id"]?.GetValue<string>() is { } id && message["usage"] is { } usage && _messages.Add(id))
                {
                    var cacheRead = CliJson.Long(usage["cache_read_input_tokens"]);
                    var input = CliJson.Long(usage["input_tokens"]) + cacheRead + CliJson.Long(usage["cache_creation_input_tokens"]);
                    if (input + CliJson.Long(usage["output_tokens"]) > 0) yield return new CliModelCall(input, CliJson.Long(usage["output_tokens"]), cacheRead);
                }
                foreach (var block in message?["content"]?.AsArray() ?? [])
                {
                    switch (block?["type"]?.GetValue<string>())
                    {
                        case "text" when block["text"]?.GetValue<string>() is { Length: > 0 } text:
                            yield return new CliText(text);
                            break;
                        case "tool_use":
                            yield return new CliToolUse(block["name"]?.GetValue<string>() ?? "tool", CliJson.Summarize(block["input"]));
                            break;
                    }
                }
                break;
            case "result":
                var totals = node["usage"];
                long? cached = totals is null ? null : CliJson.Long(totals["cache_read_input_tokens"]);
                long? totalInput = totals is null ? null : CliJson.Long(totals["input_tokens"]) + cached + CliJson.Long(totals["cache_creation_input_tokens"]);
                yield return new CliResult(
                    node["result"]?.GetValue<string>() ?? node["subtype"]?.GetValue<string>() ?? "",
                    node["is_error"]?.GetValue<bool>() == true || node["subtype"]?.GetValue<string>() is { } subtype && subtype.StartsWith("error", StringComparison.Ordinal),
                    node["total_cost_usd"] is JsonValue cost && cost.TryGetValue<decimal>(out var usd) ? usd : null,
                    totalInput, totals is null ? null : CliJson.Long(totals["output_tokens"]), cached,
                    node["num_turns"] is JsonValue turns && turns.TryGetValue<int>(out var n) ? n : null);
                break;
        }
    }
}

/// <summary>
/// Cursor CLI (<c>cursor-agent -p</c>). Writes need <c>--force</c>, which approves every shell command
/// that is not denied, so the workspace permissions file allows the repository's commands and denies
/// git, network and environment tools. Cursor reports no token usage; cost is billed by the Cursor plan.
/// </summary>
public sealed class CursorCliAdapter(string executable) : ICodingCliAdapter
{
    /// <summary>Shell commands Cursor agents may never run (git is the platform's; the rest can exfiltrate).</summary>
    public static readonly string[] DeniedCommands = ["git", "curl", "wget", "ssh", "scp", "sftp", "nc", "ncat", "telnet", "sudo", "su", "env", "printenv"];

    public AgentRuntime Runtime => AgentRuntime.CursorCli;
    public string DisplayName => "Cursor CLI";
    public string Executable => executable;

    public (IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment) Prepare(CliInvocation invocation)
    {
        var allow = new JsonArray("Read(**)");
        if (!invocation.ReadOnly)
        {
            allow.Add("Write(**)");
            foreach (var command in invocation.AllowedCommands.Where(c => !DeniedCommands.Contains(c))) allow.Add($"Shell({command})");
        }
        var deny = new JsonArray([.. DeniedCommands.Select(c => (JsonNode?)JsonValue.Create($"Shell({c})")),
            JsonValue.Create("Write(.cursor/**)"), JsonValue.Create("Write(**/.git/**)")]);
        var config = Path.Combine(invocation.WorkingDirectory, ".cursor");
        CliJson.WriteFile(config, "cli.json", new JsonObject { ["permissions"] = new JsonObject { ["allow"] = allow, ["deny"] = deny } }.ToJsonString(CliJson.Options));
        if (invocation.McpServers.Count > 0)
            CliJson.WriteFile(config, "mcp.json", CliJson.McpDocument(invocation.McpServers).ToJsonString(CliJson.Options));

        // No system prompt flag: the instructions lead the prompt.
        var prompt = $"{invocation.Instructions.Trim()}\n\n---\n\n{invocation.Prompt}";
        var arguments = new List<string> { "-p", prompt, "--output-format", "stream-json", "--model", invocation.Model };
        if (!invocation.ReadOnly) arguments.Add("--force");
        var environment = new Dictionary<string, string>(invocation.Credentials) { ["HOME"] = invocation.HomeDirectory };
        return (arguments, environment);
    }

    public IEnumerable<CliEvent> Parse(string line)
    {
        var node = CliJson.TryParse(line);
        switch (node?["type"]?.GetValue<string>())
        {
            case "assistant":
                foreach (var block in node["message"]?["content"]?.AsArray() ?? [])
                    if (block?["type"]?.GetValue<string>() == "text" && block["text"]?.GetValue<string>() is { Length: > 0 } text) yield return new CliText(text);
                break;
            case "tool_call" when node["subtype"]?.GetValue<string>() == "started" && node["tool_call"] is JsonObject call && call.Count > 0:
                // {"tool_call":{"shellToolCall":{"args":{...}}}} → shell
                var (key, value) = call.First();
                var name = key.EndsWith("ToolCall", StringComparison.Ordinal) ? key[..^"ToolCall".Length] : key;
                yield return new CliToolUse(name, CliJson.Summarize(value?["args"] ?? value));
                break;
            case "result":
                var usage = node["usage"];
                yield return new CliResult(
                    node["result"]?.GetValue<string>() ?? "",
                    node["is_error"]?.GetValue<bool>() == true || node["subtype"]?.GetValue<string>() is { } subtype && subtype != "success",
                    null,
                    usage is null ? null : CliJson.Long(usage["input_tokens"] ?? usage["inputTokens"]),
                    usage is null ? null : CliJson.Long(usage["output_tokens"] ?? usage["outputTokens"]),
                    usage is null ? null : CliJson.Long(usage["cache_read_input_tokens"] ?? usage["cacheReadTokens"]),
                    null);
                break;
        }
    }
}
