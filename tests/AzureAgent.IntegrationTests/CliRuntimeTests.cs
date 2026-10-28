using System.Net;
using System.Text.Json.Nodes;
using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Features.Agents;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Application.Features.Projects;
using AzureAgent.Domain.Agents;
using Shouldly;

namespace AzureAgent.IntegrationTests;

/// <summary>Claude Code and Cursor CLI agents, driven by fake executables that speak each CLI's stream-json.</summary>
[Collection(PlatformCollection.Name)]
public sealed class CliRuntimeTests(AppFactory factory) : PlatformTestBase(factory)
{
    private string LogDir(string name) => Directory.CreateDirectory(Path.Combine(Factory.Root, "cli-log", name)).FullName;

    private async Task<(Api Admin, ProjectDto Project, FakeAzureDevOpsHandle Azure)> ProjectWithRepoAsync(string key, string repo, int workItem, string title)
    {
        var (admin, _) = await AdminAsync();
        var project = await CreateProjectAsync(admin, key);
        var azure = Factory.Azure.Project(key);
        var bare = Factory.CreateRemoteRepository(azure, repo);
        azure.AddWorkItem(workItem, title, "Active", tags: $"repo:{repo}");
        await admin.Post<SyncResult>($"/api/projects/{project.Id}/azure/sync", new { });
        return (admin, project, new FakeAzureDevOpsHandle(azure, bare));
    }

    public sealed record FakeAzureDevOpsHandle(Fakes.FakeAzureDevOps Azure, string Bare);

    private static Task<AgentDto> CreateAgentAsync(Api admin, Guid projectId, string name, AgentRuntime runtime, string model, object? settings = null,
        string kind = "Developer", string provider = "Anthropic") =>
        admin.Post<AgentDto>($"/api/projects/{projectId}/agents", new
        {
            name, kind, provider, model, runtime, enabled = true, maxConcurrentRuns = 1,
            settings = settings ?? new { maxIterations = 30, maxTokensPerRun = 400_000, maxCostPerRunUsd = 5m, timeoutMinutes = 5 },
        });

    private static async Task<Application.Features.Runs.RunDetailDto> StartAsync(Api admin, Guid projectId, Guid agentId, int workItem)
    {
        var started = await admin.Post<System.Text.Json.JsonElement>($"/api/projects/{projectId}/runs", new { agentId, workItemAzureId = workItem });
        var id = started.GetProperty("id").GetGuid();
        return await WaitForRunAsync(admin, projectId, r => r.Id == id);
    }

    [Fact]
    public async Task Claude_code_agent_implements_in_the_workspace_with_metered_usage_and_no_server_secrets()
    {
        var (admin, project, handle) = await ProjectWithRepoAsync("CLAUDE", "paging", 1101, "Add paging helper");
        var log = LogDir("claude");
        Environment.SetEnvironmentVariable("AZURE_AGENT_SERVER_SECRET", "must-not-leak");
        Factory.SetCliScenario("claude", $$$"""
            printf '%s\n' "$@" > "{{{log}}}/args.txt"
            env > "{{{log}}}/env.txt"
            pwd > "{{{log}}}/cwd.txt"
            echo 'public static class Paging { public static int Pages(int n, int size) => (n + size - 1) / size; }' > paging/Paging.cs
            cat <<'JSON'
            {"type":"system","subtype":"init","model":"claude-sonnet-4-5"}
            {"type":"assistant","message":{"id":"m1","usage":{"input_tokens":1000,"cache_read_input_tokens":0,"cache_creation_input_tokens":0,"output_tokens":200},"content":[{"type":"tool_use","name":"Write","input":{"file_path":"paging/Paging.cs"}}]}}
            {"type":"user","message":{"content":[{"type":"tool_result","content":"ok"}]}}
            {"type":"assistant","message":{"id":"m2","usage":{"input_tokens":2000,"cache_read_input_tokens":1000,"cache_creation_input_tokens":0,"output_tokens":300},"content":[{"type":"tool_use","name":"Bash","input":{"command":"dotnet test"}}]}}
            {"type":"assistant","message":{"id":"m2","usage":{"input_tokens":2000,"cache_read_input_tokens":1000,"cache_creation_input_tokens":0,"output_tokens":300},"content":[{"type":"text","text":"All green"}]}}
            {"type":"result","subtype":"success","is_error":false,"result":"Added Paging.Pages and ran dotnet test.","total_cost_usd":0.5,"num_turns":2,"usage":{"input_tokens":3500,"cache_read_input_tokens":1000,"cache_creation_input_tokens":0,"output_tokens":600}}
            JSON
            """);
        try
        {
            var agent = await CreateAgentAsync(admin, project.Id, "Claude CLI", AgentRuntime.ClaudeCode, "claude-sonnet-4-5");
            agent.Runtime.ShouldBe(AgentRuntime.ClaudeCode);
            var run = await StartAsync(admin, project.Id, agent.Id, 1101);

            run.Run.Error.ShouldBeNull();
            run.Run.Status.ShouldBe(RunStatus.Succeeded);
            run.Summary.ShouldBe("Added Paging.Pages and ran dotnet test.");
            run.Run.PullRequestUrls.ShouldHaveSingleItem();
            AppFactory.Git(Factory.Root, "--git-dir", handle.Bare, "show", "--stat", "ab/1101-add-paging-helper").ShouldContain("Paging.cs");
            // Two API responses metered as they streamed, plus the final totals' remainder (500 in, 100 out).
            run.ModelCalls.ShouldBe(2);
            run.Run.InputTokens.ShouldBe(4500);
            run.Run.OutputTokens.ShouldBe(600);
            run.CachedInputTokens.ShouldBe(1000);
            // claude-sonnet: 3 USD/1M input, 0.3 cached, 15 output → 0.006 + 0.0108 + 0.003
            run.Run.CostUsd.ShouldBe(0.0198m);
            run.ToolCalls.ShouldBe(2);
            run.Steps.Where(s => s.Type == RunStepType.ToolCall).Select(s => s.Title).ShouldBe(["Write", "Bash"]);
            run.Steps.ShouldContain(s => s.Type == RunStepType.Log && s.Title == "Started Claude Code CLI");

            var args = File.ReadAllLines(Path.Combine(log, "args.txt"));
            args[0].ShouldBe("-p");
            args[1].ShouldStartWith("Implement Azure Boards User Story #1101: Add paging helper");
            args[Array.IndexOf(args, "--permission-mode") + 1].ShouldBe("dontAsk");
            args.ShouldContain("Bash(dotnet:*)");
            args[Array.IndexOf(args, "--append-system-prompt-file") + 1].ShouldEndWith(".home/instructions.md");
            var env = File.ReadAllText(Path.Combine(log, "env.txt"));
            env.ShouldContain("ANTHROPIC_API_KEY=sk-ant-test");
            env.ShouldNotContain("must-not-leak");
            env.ShouldNotContain("GIT_CONFIG_VALUE_0"); // the Azure credential stays with the platform's git
            env.ShouldNotContain("pat-123456");
            env.ShouldMatch(@"HOME=\S+\.home");
            File.ReadAllText(Path.Combine(log, "cwd.txt")).Trim().ShouldEndWith(run.Run.Id.ToString("N"));
        }
        finally { Environment.SetEnvironmentVariable("AZURE_AGENT_SERVER_SECRET", null); }
    }

    [Fact]
    public async Task Claude_code_agents_prefer_the_subscription_token_and_reviews_run_read_only()
    {
        var (admin, project, handle) = await ProjectWithRepoAsync("CLREV", "shop", 1201, "Review me");
        (await admin.Send(HttpMethod.Put, $"/api/projects/{project.Id}/secrets/CLAUDE_CODE_OAUTH_TOKEN", new { value = "oauth-token-1" })).EnsureSuccessStatusCode();
        var log = LogDir("claude-review");
        Factory.SetCliScenario("claude", $$$"""
            printf '%s\n' "$@" > "{{{log}}}/args.txt"
            env > "{{{log}}}/env.txt"
            cat <<'JSON'
            {"type":"assistant","message":{"id":"r1","usage":{"input_tokens":500,"output_tokens":50},"content":[{"type":"text","text":"reviewing"}]}}
            {"type":"result","subtype":"success","is_error":false,"result":"{\"verdict\":\"request_changes\",\"summary\":\"Division by zero.\",\"findings\":[{\"file\":\"/src/Pages.cs\",\"line\":1,\"severity\":\"major\",\"message\":\"size can be 0\"}]}","usage":{"input_tokens":500,"output_tokens":50}}
            JSON
            """);
        await CreateAgentAsync(admin, project.Id, "Claude reviewer", AgentRuntime.ClaudeCode, "claude-sonnet-4-5", kind: "Reviewer");
        handle.Azure.FileContents["/src/Pages.cs"] = "public static int Pages(int n, int size) => n / size;\n";
        handle.Azure.PullRequests.Add((new AzurePullRequest(70, "Paging", "active", "refs/heads/feature/paging", "refs/heads/main", "Ayşe", DateTimeOffset.UtcNow,
            null, "abc", "shop"), 1201));
        await admin.Post<SyncResult>($"/api/projects/{project.Id}/azure/sync", new { });
        var review = await WaitForRunAsync(admin, project.Id, r => r.Kind == RunKind.Review && r.AgentName == "Claude reviewer");

        review.Run.Error.ShouldBeNull();
        review.Summary.ShouldBe("RequestChanges: Division by zero.");
        handle.Azure.Threads.ShouldContain(t => t.PullRequestId == 70 && t.File == "/src/Pages.cs" && t.Content.Contains("size can be 0"));
        var allowed = File.ReadAllLines(Path.Combine(log, "args.txt")).SkipWhile(a => a != "--allowedTools").Skip(1).ToList();
        allowed.ShouldBe(["Read", "Glob", "Grep", "LS"]);
        var env = File.ReadAllText(Path.Combine(log, "env.txt"));
        env.ShouldContain("CLAUDE_CODE_OAUTH_TOKEN=oauth-token-1");
        env.ShouldNotContain("ANTHROPIC_API_KEY");
    }

    [Fact]
    public async Task Cli_agents_cannot_tamper_with_git_metadata_or_outrun_their_limits()
    {
        var (admin, project, _) = await ProjectWithRepoAsync("CLSAFE", "core", 1301, "Harmless change");
        var agent = await CreateAgentAsync(admin, project.Id, "Tamperer", AgentRuntime.ClaudeCode, "claude-sonnet-4-5",
            new { maxIterations = 30, maxTokensPerRun = 5_000, maxCostPerRunUsd = 5m, timeoutMinutes = 5 });

        // Rewriting the remote URL would redirect the platform's authenticated push.
        Factory.SetCliScenario("claude", """
            echo 'x' > core/X.cs
            printf '[url "https://evil.example/"]\n\tinsteadOf = https://dev.azure.com/\n' >> core/.git/config
            echo '{"type":"result","subtype":"success","is_error":false,"result":"done","usage":{"input_tokens":10,"output_tokens":5}}'
            """);
        var tampered = await StartAsync(admin, project.Id, agent.Id, 1301);
        tampered.Run.Status.ShouldBe(RunStatus.Failed);
        tampered.Run.Error.ShouldBe("The agent changed git configuration or hooks of core; the run was discarded");
        tampered.Run.PullRequestUrls.ShouldBeEmpty();

        // Usage over the per-run token limit stops the CLI immediately (it would otherwise sleep for a minute).
        Factory.SetCliScenario("claude", """
            echo '{"type":"assistant","message":{"id":"big","usage":{"input_tokens":9000,"output_tokens":100},"content":[{"type":"text","text":"thinking"}]}}'
            sleep 60
            echo '{"type":"result","subtype":"success","is_error":false,"result":"too late"}'
            """);
        var started = DateTime.UtcNow;
        var limited = await StartAsync(admin, project.Id, agent.Id, 1301);
        limited.Run.Status.ShouldBe(RunStatus.Failed);
        limited.Run.Error.ShouldBe("Token limit exceeded (9100 > 5000)");
        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(30));

        // A CLI error result fails the run with the CLI's message.
        Factory.SetCliScenario("claude", """
            echo '{"type":"result","subtype":"success","is_error":true,"result":"Not logged in · Please run /login"}'
            """);
        (await StartAsync(admin, project.Id, agent.Id, 1301)).Run.Error.ShouldBe("Claude Code CLI: Not logged in · Please run /login");
    }

    [Fact]
    public async Task Cursor_agent_implements_with_workspace_permissions_and_reports_missing_usage()
    {
        var (admin, project, handle) = await ProjectWithRepoAsync("CURSOR", "ui", 1401, "Dark mode toggle");
        (await admin.Send(HttpMethod.Put, $"/api/projects/{project.Id}/secrets/CURSOR_API_KEY", new { value = "cursor-key-1" })).EnsureSuccessStatusCode();
        var log = LogDir("cursor");
        Factory.SetCliScenario("cursor", """
            printf '%s\n' "$@" > "LOG_DIR/args.txt"
            env > "LOG_DIR/env.txt"
            cp .cursor/cli.json "LOG_DIR/cli.json"
            echo 'export const dark = true;' > ui/theme.ts
            cat <<'JSON'
            {"type":"system","subtype":"init","model":"sonnet-4.5","session_id":"s1"}
            {"type":"tool_call","subtype":"started","call_id":"c1","tool_call":{"writeToolCall":{"args":{"path":"ui/theme.ts"}}},"session_id":"s1"}
            {"type":"tool_call","subtype":"completed","call_id":"c1","tool_call":{"writeToolCall":{"args":{"path":"ui/theme.ts"},"result":{"success":{}}}},"session_id":"s1"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Added the toggle."}]},"session_id":"s1"}
            {"type":"result","subtype":"success","is_error":false,"duration_ms":4200,"result":"Added a dark mode flag.","session_id":"s1"}
            JSON
            """.Replace("LOG_DIR", log));
        var agent = await CreateAgentAsync(admin, project.Id, "Cursor", AgentRuntime.CursorCli, "sonnet-4.5", provider: "OpenAI");
        var run = await StartAsync(admin, project.Id, agent.Id, 1401);

        run.Run.Error.ShouldBeNull();
        run.Run.Status.ShouldBe(RunStatus.Succeeded);
        run.Summary.ShouldBe("Added a dark mode flag.");
        AppFactory.Git(Factory.Root, "--git-dir", handle.Bare, "show", "--stat", "ab/1401-dark-mode-toggle").ShouldContain("theme.ts");
        AppFactory.Git(Factory.Root, "--git-dir", handle.Bare, "show", "--stat", "ab/1401-dark-mode-toggle").ShouldNotContain(".cursor");
        run.Run.CostUsd.ShouldBe(0m);
        run.Steps.ShouldContain(s => s.Type == RunStepType.ToolCall && s.Title == "write");
        run.Steps.ShouldContain(s => s.Title == "Usage not reported");
        var args = File.ReadAllLines(Path.Combine(log, "args.txt"));
        args.ShouldContain("--force");
        args[Array.IndexOf(args, "--model") + 1].ShouldBe("sonnet-4.5");
        var deny = JsonNode.Parse(File.ReadAllText(Path.Combine(log, "cli.json")))!["permissions"]!["deny"]!.AsArray().Select(n => n!.GetValue<string>());
        deny.ShouldContain("Shell(git)");
        File.ReadAllText(Path.Combine(log, "env.txt")).ShouldContain("CURSOR_API_KEY=cursor-key-1");
    }

    [Fact]
    public async Task Runtimes_are_listed_with_their_installed_versions_and_validated()
    {
        var (admin, _) = await AdminAsync();
        var project = await CreateProjectAsync(admin, "RTIME");
        Factory.SetCliScenario("claude", """[ "$1" = "--version" ] && echo "2.1.312 (Claude Code)" """);
        Factory.SetCliScenario("cursor", """[ "$1" = "--version" ] && echo "2026.10.1" """);
        var runtimes = await admin.Get<List<AgentRuntimeInfo>>("/api/agent-runtimes");
        runtimes.Select(r => r.Runtime).ShouldBe([AgentRuntime.Framework, AgentRuntime.ClaudeCode, AgentRuntime.CursorCli]);
        runtimes.Single(r => r.Runtime == AgentRuntime.ClaudeCode).ShouldSatisfyAllConditions(r => r.Available.ShouldBeTrue(), r => r.Version.ShouldBe("2.1.312 (Claude Code)"));

        // Claude Code runs Anthropic models; CLI model names are plain identifiers.
        var wrongProvider = await admin.Send(HttpMethod.Post, $"/api/projects/{project.Id}/agents", new
        {
            name = "x", kind = "Developer", provider = "OpenAI", model = "gpt-5", runtime = "ClaudeCode", enabled = true, maxConcurrentRuns = 1,
        });
        wrongProvider.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await wrongProvider.Content.ReadAsStringAsync()).ShouldContain("Claude Code agents use the Anthropic provider");
        (await admin.Send(HttpMethod.Post, $"/api/projects/{project.Id}/agents", new
        {
            name = "y", kind = "Developer", provider = "Anthropic", model = "sonnet --dangerously-skip-permissions", runtime = "ClaudeCode", enabled = true, maxConcurrentRuns = 1,
        })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
