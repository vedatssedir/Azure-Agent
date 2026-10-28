using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Application.Features.Costs;
using AzureAgent.Application.Features.Secrets;
using AzureAgent.Domain.Agents;
using AzureAgent.Domain.Azure;
using AzureAgent.Domain.Projects;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureAgent.Infrastructure.AgentRuntime;

// The enum shares its name with this namespace.
using AgentRuntime = AzureAgent.Domain.Agents.AgentRuntime;

public sealed class AgentRuntimeOptions
{
    public const string Section = "AgentRuntime";
    public string WorkspaceRoot { get; set; } = Path.Combine(Path.GetTempPath(), "azure-agent-workspaces");
    /// <summary>Where the full git mirrors of every synced repository live (code browser, history).</summary>
    public string MirrorRoot { get; set; } = Path.Combine(Path.GetTempPath(), "azure-agent-mirrors");
    public bool KeepWorkspaces { get; set; }
    /// <summary>Executable of the Claude Code CLI (npm package @anthropic-ai/claude-code).</summary>
    public string ClaudeCliPath { get; set; } = "claude";
    /// <summary>Executable of the Cursor CLI.</summary>
    public string CursorCliPath { get; set; } = "cursor-agent";
    /// <summary>Server environment variables child processes (git, build commands, CLI agents) may see. Everything else is withheld.</summary>
    public List<string> InheritedEnvironment { get; set; } =
    [
        "PATH", "LANG", "LC_ALL", "TZ", "TMPDIR", "TERM",
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "no_proxy",
        "SSL_CERT_FILE", "SSL_CERT_DIR", "NODE_EXTRA_CA_CERTS", "REQUESTS_CA_BUNDLE",
        "DOTNET_ROOT", "JAVA_HOME",
    ];
}

public sealed class RunLimitExceededException(string message) : Exception(message);

/// <summary>Executes one agent run end to end and records every step, token and cent.</summary>
public sealed partial class AgentRunExecutor(
    IAppDbContext db,
    IClock clock,
    SecretResolver secrets,
    AzureConnector connector,
    BudgetGuard budgets,
    PriceList prices,
    IChatClientFactory chatClients,
    IProcessRunner processes,
    ICodingCliFactory cliFactory,
    IOptions<AgentRuntimeOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<AgentRunExecutor> logger)
{
    private int _sequence;

    public async Task ExecuteAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.AgentRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || run.Status != RunStatus.Queued)
        {
            logger.LogInformation("Run {RunId} is not queued (status {Status}); skipping", runId, run?.Status);
            return;
        }
        var agent = await db.Agents.FirstAsync(a => a.Id == run.AgentId, ct);
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["RunId"] = run.Id, ["AgentId"] = agent.Id, ["ProjectId"] = run.ProjectId });

        if (!agent.Enabled) { await FailAsync(run, "Agent is disabled", ct); return; }
        if (await budgets.ExhaustedAsync(agent, run.ProjectId, ct) is { } exhausted) { await FailAsync(run, exhausted, ct); return; }
        var active = await db.AgentRuns.CountAsync(r => r.AgentId == agent.Id && r.Status == RunStatus.Running, ct);
        if (active >= agent.MaxConcurrentRuns)
        {
            // Leave it queued; the stale-run job re-dispatches queued runs.
            logger.LogInformation("Agent {Agent} is at its concurrency limit; run stays queued", agent.Name);
            return;
        }

        run.Start(clock.UtcNow);
        run.Provider = agent.Provider.ToString();
        run.Model = agent.Model;
        await db.SaveChangesAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(agent.Settings.TimeoutMinutes));
        var runDir = Path.Combine(options.Value.WorkspaceRoot, run.OrganizationId.ToString("N"), run.Id.ToString("N"));
        // Private HOME for every process of the run (git, build tools, CLI agents): nothing leaks between runs or tenants.
        var homeDir = runDir + ".home";
        try
        {
            Directory.CreateDirectory(homeDir);
            if (run.Kind == RunKind.Implement) await ImplementAsync(run, agent, runDir, homeDir, timeout.Token);
            else await ReviewAsync(run, agent, runDir, homeDir, timeout.Token);
            run.Succeed(clock.UtcNow, run.Summary);
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogInformation("Run {RunId} succeeded: {Tokens} tokens, {Cost} USD", run.Id, run.InputTokens + run.OutputTokens, run.CostUsd);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var message = e is OperationCanceledException ? $"Run timed out after {agent.Settings.TimeoutMinutes} minutes" : e.Message;
            logger.LogWarning(e, "Run {RunId} failed", run.Id);
            await FailAsync(run, message, CancellationToken.None);
        }
        finally
        {
            if (!options.Value.KeepWorkspaces && Directory.Exists(runDir)) TryDelete(runDir);
            if (Directory.Exists(homeDir)) TryDelete(homeDir);
        }
    }

    private async Task FailAsync(AgentRun run, string error, CancellationToken ct)
    {
        await AddStepAsync(run, RunStepType.Error, "Run failed", error, ct: ct);
        if (run.Status == RunStatus.Queued) run.Start(clock.UtcNow);
        run.Fail(clock.UtcNow, error);
        await db.SaveChangesAsync(ct);
    }

    // Implement -------------------------------------------------------------------------------

    private async Task ImplementAsync(AgentRun run, Agent agent, string runDir, string homeDir, CancellationToken ct)
    {
        var item = await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == run.WorkItemId, ct);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == run.ProjectId, ct);
        var names = item.RepositoryNames.Select(n => n.ToLowerInvariant()).ToList();
        var repositories = (await db.Repositories.AsNoTracking().Where(r => r.ProjectId == project.Id).ToListAsync(ct))
            .Where(r => names.Contains(r.Name.ToLowerInvariant())).ToList();
        if (repositories.Count == 0)
            throw new InvalidOperationException($"Work item #{item.AzureId} names no known repository. Tag it repo:<name> (tags: {string.Join(", ", item.Tags)})");
        foreach (var repo in repositories)
        {
            if (!repo.Policy.AgentsEnabled) throw new InvalidOperationException($"Agents are disabled for repository {repo.Name}");
            if (repo.DefaultBranch is null) throw new InvalidOperationException($"Repository {repo.Name} is empty");
            if (await OwnerWithoutWriteAsync(project, agent, repo, ct) is { } owner)
                throw new InvalidOperationException($"{owner} does not have '{Permission.RepositoryAgentWrite}' on {repo.Name}");
        }

        var credential = await connector.ServiceCredentialAsync(project.OrganizationId, ct);
        var git = new GitWorkspace(processes, project.AzureOrganizationUrl, credential, agent.Name, $"agent-{agent.Id:N}@azure-agent.local", homeDir);
        var branch = WorkItemMapping.BranchName(item.AzureId, item.Title);
        run.Branch = branch;
        if (repositories.SelectMany(r => r.Policy.ProtectedBranches).Any(p => BranchMatches(p, branch)))
            throw new InvalidOperationException($"Branch {branch} is protected");

        foreach (var repo in repositories)
        {
            await git.CloneAsync(runDir, repo.RemoteUrl, repo.Name, repo.DefaultBranch!, ct);
            await git.RunAsync(Path.Combine(runDir, repo.Name), ct, "checkout", "-b", branch);
            await AddStepAsync(run, RunStepType.Git, $"Cloned {repo.Name}", $"{repo.DefaultBranch} → {branch}", ct: ct);
        }

        var allowed = repositories.SelectMany(r => r.Policy.AllowedCommands).ToHashSet(StringComparer.Ordinal);
        var prompt = ImplementationPrompt(item, repositories, branch);
        var instructions = Instructions(agent, repositories);
        var settings = await db.Organizations.AsNoTracking().Where(o => o.Id == project.OrganizationId).Select(o => o.Settings).FirstAsync(ct);
        if (settings.UpdateWorkItemState) await MoveWorkItemAsync(run, project, item, "InProgress", onlyFrom: "Proposed", ct);
        string summary;
        if (agent.Runtime == AgentRuntime.Framework)
        {
            var tools = new WorkspaceTools(runDir, allowed, processes, (tool, detail) => run.ToolCalls++, homeDir).Create();
            summary = await RunAgentAsync(run, agent, project, instructions, tools, prompt, ct);
        }
        else
        {
            // The CLI edits the clones directly; git metadata must come out of the run exactly as it went in.
            var gitState = repositories.ToDictionary(r => r.Name, r => GitMetadataFingerprint(Path.Combine(runDir, r.Name)));
            summary = await RunCliAsync(run, agent, project, instructions, prompt, runDir, homeDir, allowed, readOnly: false, ct);
            foreach (var repo in repositories)
                if (GitMetadataFingerprint(Path.Combine(runDir, repo.Name)) != gitState[repo.Name])
                    throw new InvalidOperationException($"The agent changed git configuration or hooks of {repo.Name}; the run was discarded");
        }
        run.Summary = summary;

        var (_, azure) = await connector.ConnectAsync(project.Id, ct);
        foreach (var repo in repositories)
        {
            var dir = Path.Combine(runDir, repo.Name);
            var changed = await git.ChangedFilesAsync(dir, ct);
            if (changed.Count > repo.Policy.MaxFilesChangedPerRun)
                throw new InvalidOperationException($"{repo.Name}: {changed.Count} files changed, policy allows {repo.Policy.MaxFilesChangedPerRun}");
            if (changed.Count > 0)
            {
                await git.RunAsync(dir, ct, "add", "-A");
                await git.RunAsync(dir, ct, "commit", "-m", $"AB#{item.AzureId}: {item.Title}", "-m", Truncate(summary, 2000));
            }
            var ahead = int.Parse(await git.RunAsync(dir, ct, "rev-list", "--count", $"origin/{repo.DefaultBranch}..HEAD"));
            if (ahead == 0) continue;
            await git.RunAsync(dir, ct, "push", "-u", "origin", branch);
            var pr = await azure.CreatePullRequestAsync(repo.Name, branch, repo.DefaultBranch!, $"AB#{item.AzureId}: {item.Title}",
                $"Implemented by agent **{agent.Name}** for [#{item.AzureId}]({item.WebUrl}).\n\n{summary}", item.AzureId, ct);
            var url = azure.PullRequestWebUrl(repo.WebUrl, pr.Id);
            run.PullRequestUrls = [.. run.PullRequestUrls, url];
            db.PullRequests.Add(new PullRequest
            {
                OrganizationId = run.OrganizationId, RepositoryId = repo.Id, AzureId = pr.Id, Title = pr.Title, SourceBranch = branch,
                TargetBranch = repo.DefaultBranch!, CreatedBy = agent.Name, CreatedByAgent = true, OpenedAt = clock.UtcNow, WebUrl = url,
                WorkItemAzureIds = [item.AzureId],
            });
            await AddStepAsync(run, RunStepType.Git, $"Opened pull request in {repo.Name}", url, ct: ct);
        }
        if (run.PullRequestUrls.Count == 0) throw new InvalidOperationException("The agent made no changes");

        var links = string.Join(", ", run.PullRequestUrls.Select(u => $"<a href=\"{u}\">{u}</a>"));
        await azure.AddWorkItemCommentAsync(item.AzureId, $"Agent <b>{agent.Name}</b> opened {links}<br/>Cost: {run.CostUsd:0.0000} USD, {run.InputTokens + run.OutputTokens} tokens.", ct);
        if (settings.UpdateWorkItemState) await MoveWorkItemAsync(run, project, item, "Resolved", onlyFrom: null, ct);
    }

    /// <summary>
    /// Moves the work item to the first state of a category (In Progress when the agent starts,
    /// Resolved when its PR opens). Processes without such a state (Basic has no Resolved) are left alone.
    /// </summary>
    private async Task MoveWorkItemAsync(AgentRun run, Project project, WorkItem item, string category, string? onlyFrom, CancellationToken ct)
    {
        var current = project.WorkItemTypes.FirstOrDefault(t => t.Name == item.Type)?.States.FirstOrDefault(s => s.Name == item.State)?.Category;
        if (onlyFrom is not null && current != onlyFrom) return;
        if (project.StateInCategory(item.Type, category) is not { } state || state == item.State) return;
        try
        {
            var (_, azure) = await connector.ConnectAsync(project.Id, ct);
            await azure.UpdateWorkItemFieldsAsync(item.AzureId, new Dictionary<string, object?> { ["System.State"] = state }, ct);
            await AddStepAsync(run, RunStepType.Log, $"Work item moved to {state}", null, ct: ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Azure rules (required fields, transitions) can refuse the change; that must not fail the agent's work.
            await AddStepAsync(run, RunStepType.Log, $"Could not move the work item to {state}", e.Message, ct: ct);
        }
    }

    /// <summary>
    /// The agent works with its owner's rights: their project role and repository rules (organization
    /// admins hold every permission). Returns the owner's e-mail when they may not let agents write here.
    /// </summary>
    private async Task<string?> OwnerWithoutWriteAsync(Project project, Agent agent, Repository repo, CancellationToken ct)
    {
        if (agent.OwnerUserId is not { } ownerId) return null;
        var email = await db.Users.AsNoTracking().Where(u => u.Id == ownerId).Select(u => u.Email).FirstAsync(ct);
        if (await db.OrganizationMembers.AsNoTracking().AnyAsync(m => m.UserId == ownerId && m.OrganizationId == project.OrganizationId
                && (m.Role == Domain.Identity.OrganizationRole.Admin || m.Role == Domain.Identity.OrganizationRole.Owner), ct)) return null;
        var member = await db.ProjectMembers.AsNoTracking().FirstOrDefaultAsync(m => m.ProjectId == project.Id && m.UserId == ownerId, ct);
        if (member is null) return email;
        var permissions = RolePermissions.For(member.Role).ToHashSet();
        var rules = await db.RepositoryAccessRules.AsNoTracking()
            .Where(r => r.RepositoryId == repo.Id && r.Permission == Permission.RepositoryAgentWrite && (r.UserId == member.UserId || r.Role == member.Role)).ToListAsync(ct);
        if (rules.Any(r => r.Effect == AccessEffect.Allow)) permissions.Add(Permission.RepositoryAgentWrite);
        if (rules.Any(r => r.Effect == AccessEffect.Deny)) permissions.Remove(Permission.RepositoryAgentWrite);
        return permissions.Contains(Permission.RepositoryAgentWrite) ? null : email;
    }

    private static string ImplementationPrompt(WorkItem item, IReadOnlyList<Repository> repositories, string branch)
    {
        var builder = new StringBuilder()
            .AppendLine($"Implement Azure Boards {item.Type} #{item.AzureId}: {item.Title}")
            .AppendLine($"Work item: {item.WebUrl}")
            .AppendLine()
            .AppendLine("Description:")
            .AppendLine(WorkItemMapping.HtmlToText(item.Description) is { Length: > 0 } d ? d : "(no description)");
        if (item.AcceptanceCriteria.Length > 0) builder.AppendLine().AppendLine("Acceptance criteria:").AppendLine(WorkItemMapping.HtmlToText(item.AcceptanceCriteria));
        builder.AppendLine().AppendLine($"Repositories (folders in the workspace; branch {branch} is checked out):");
        foreach (var repo in repositories) builder.AppendLine($"- {repo.Name}/");
        return builder.ToString();
    }

    // Review ------------------------------------------------------------------------------------

    private async Task ReviewAsync(AgentRun run, Agent agent, string runDir, string homeDir, CancellationToken ct)
    {
        var pr = await db.PullRequests.FirstAsync(p => p.Id == run.PullRequestId, ct);
        var repo = await db.Repositories.AsNoTracking().FirstAsync(r => r.Id == pr.RepositoryId, ct);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == run.ProjectId, ct);
        var (_, azure) = await connector.ConnectAsync(project.Id, ct);
        var current = (await azure.ListPullRequestsAsync(repo.Name, ct)).FirstOrDefault(p => p.Id == pr.AzureId) ?? throw new InvalidOperationException("Pull request not found in Azure");
        var commit = current.LastMergeSourceCommit ?? throw new InvalidOperationException("Pull request has no source commit yet");

        var changes = await azure.GetPullRequestChangesAsync(repo.Name, pr.AzureId, ct);
        var builder = new StringBuilder($"Review pull request !{pr.AzureId} \"{pr.Title}\" in {repo.Name} ({pr.SourceBranch} → {pr.TargetBranch}).\n");
        var budget = 120_000;
        foreach (var change in changes.Take(60))
        {
            builder.AppendLine($"\n### {change.ChangeType}: {change.Path}");
            if (change.ChangeType.Contains("delete", StringComparison.OrdinalIgnoreCase)) continue;
            var content = await azure.GetFileContentAsync(repo.Name, change.Path, commit, ct);
            if (content is null) continue;
            var numbered = string.Join('\n', content.Split('\n').Select((l, i) => $"{i + 1}: {l}"));
            var slice = numbered.Length > Math.Min(budget, 20_000) ? numbered[..Math.Min(budget, 20_000)] + "\n…" : numbered;
            builder.AppendLine("```").AppendLine(slice).AppendLine("```");
            budget -= slice.Length;
            if (budget <= 0) { builder.AppendLine("\n(remaining files omitted)"); break; }
        }
        builder.AppendLine("""

            Respond with JSON only:
            {"verdict":"approve|comment|request_changes","summary":"...","findings":[{"file":"/path","line":12,"severity":"blocker|major|minor|nit","message":"..."}]}
            Report real defects (bugs, security, missing tests for changed behaviour), not style preferences.
            """);

        var reviewInstructions = Instructions(agent, [repo]);
        string text;
        if (agent.Runtime == AgentRuntime.Framework)
        {
            text = await RunAgentAsync(run, agent, project, reviewInstructions, [], builder.ToString(), ct);
        }
        else
        {
            // The changed files are in the prompt; the CLI runs read-only in an empty directory.
            Directory.CreateDirectory(runDir);
            text = await RunCliAsync(run, agent, project, reviewInstructions, builder.ToString(), runDir, homeDir, new HashSet<string>(), readOnly: true, ct);
        }
        var review = ParseReview(text);
        foreach (var finding in review.Findings.Take(25))
        {
            await azure.AddPullRequestThreadAsync(repo.Name, pr.AzureId, $"**{finding.Severity}** — {finding.Message}\n\n_{agent.Name}_", finding.File, finding.Line, ct);
        }
        await azure.AddPullRequestThreadAsync(repo.Name, pr.AzureId,
            $"### AI review: {review.Verdict}\n\n{review.Summary}\n\n{review.Findings.Count} finding(s) · cost {run.CostUsd:0.0000} USD\n\n_{agent.Name}_", null, null, ct);
        db.CodeReviews.Add(new CodeReview
        {
            OrganizationId = run.OrganizationId, PullRequestId = pr.Id, RunId = run.Id, SourceCommit = commit, Verdict = review.Verdict,
            Summary = review.Summary, Findings = review.Findings,
        });
        pr.LastReviewedSourceCommit = commit;
        run.Summary = $"{review.Verdict}: {review.Summary}";
    }

    public sealed record ParsedReview(ReviewVerdict Verdict, string Summary, List<ReviewFinding> Findings);

    public static ParsedReview ParseReview(string text)
    {
        var match = JsonObject().Match(text);
        if (!match.Success) return new ParsedReview(ReviewVerdict.Comment, Truncate(text.Trim(), 4000), []);
        using var json = JsonDocument.Parse(match.Value);
        var root = json.RootElement;
        var verdict = root.TryGetProperty("verdict", out var v) ? v.GetString() switch
        {
            "approve" => ReviewVerdict.Approve,
            "request_changes" => ReviewVerdict.RequestChanges,
            _ => ReviewVerdict.Comment,
        } : ReviewVerdict.Comment;
        var findings = new List<ReviewFinding>();
        if (root.TryGetProperty("findings", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in list.EnumerateArray())
            {
                findings.Add(new ReviewFinding
                {
                    File = f.TryGetProperty("file", out var file) ? file.GetString() ?? "" : "",
                    Line = f.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Number ? line.GetInt32() : null,
                    Severity = f.TryGetProperty("severity", out var s) ? s.GetString() ?? "minor" : "minor",
                    Message = f.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "",
                });
            }
        }
        return new ParsedReview(verdict, root.TryGetProperty("summary", out var summary) ? summary.GetString() ?? "" : "", findings);
    }

    // Agent invocation ----------------------------------------------------------------------------

    private async Task<string> RunAgentAsync(AgentRun run, Agent agent, Project project, string instructions, IList<AITool> tools, string prompt, CancellationToken ct)
    {
        var keyName = agent.Provider switch
        {
            ModelProvider.OpenAI => SecretNames.OpenAiApiKey,
            ModelProvider.AzureOpenAI => SecretNames.AzureOpenAiApiKey,
            ModelProvider.Anthropic => SecretNames.AnthropicApiKey,
            _ => SecretNames.GeminiApiKey,
        };
        var apiKey = await secrets.RequireAsync(project.Id, keyName, ct);
        var price = await prices.FindAsync(run.OrganizationId, agent.Provider, agent.Model, ct);
        if (price is null) logger.LogWarning("No price configured for {Provider}/{Model}; cost will be recorded as 0", agent.Provider, agent.Model);

        var allTools = tools.ToList();
        var endpoint = agent.Settings.Endpoint ?? (agent.Provider == ModelProvider.AzureOpenAI
            ? await db.Organizations.AsNoTracking().Where(o => o.Id == run.OrganizationId).Select(o => o.Settings.AzureOpenAiEndpoint).FirstAsync(ct)
            : null);

        var metered = new UsageMeteringChatClient(chatClients.Create(agent.Provider, agent.Model, apiKey, endpoint), async (usage, token) =>
        {
            var cost = price?.Cost(usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens) ?? 0;
            run.RecordUsage(usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens, cost);
            await AddStepAsync(run, RunStepType.ModelCall, $"Model call {run.ModelCalls}",
                usage.ToolCallsRequested > 0 ? $"requested {usage.ToolCallsRequested} tool call(s)" : "final answer",
                usage.InputTokens, usage.OutputTokens, cost, (int)usage.Duration.TotalMilliseconds, token);
            if (run.InputTokens + run.OutputTokens > agent.Settings.MaxTokensPerRun)
                throw new RunLimitExceededException($"Token limit exceeded ({run.InputTokens + run.OutputTokens} > {agent.Settings.MaxTokensPerRun})");
            if (run.CostUsd > agent.Settings.MaxCostPerRunUsd)
                throw new RunLimitExceededException($"Cost limit exceeded ({run.CostUsd:0.0000} > {agent.Settings.MaxCostPerRunUsd} USD)");
            if (await budgets.ExhaustedAsync(agent, run.ProjectId, token) is { } exhausted) throw new RunLimitExceededException(exhausted);
        });

        var client = metered.AsBuilder()
            .UseFunctionInvocation(loggerFactory, f =>
            {
                f.MaximumIterationsPerRequest = agent.Settings.MaxIterations;
                f.AllowConcurrentInvocation = false;
            })
            .Build();
        var aiAgent = new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Name = SafeName(agent.Name),
            ChatOptions = new ChatOptions { Instructions = instructions, Tools = allTools, Temperature = agent.Settings.Temperature, ModelId = agent.Model },
        }, loggerFactory);
        var response = await aiAgent.RunAsync(prompt, cancellationToken: ct);
        return response.Text;
    }

    // CLI agents ----------------------------------------------------------------------------------

    private const int MaxCliSteps = 400;

    /// <summary>
    /// Runs a coding CLI (Claude Code / Cursor) in the workspace. Every model request is metered as it
    /// streams; token, cost and budget limits stop the process immediately.
    /// </summary>
    private async Task<string> RunCliAsync(AgentRun run, Agent agent, Project project, string instructions, string prompt, string workDir, string homeDir,
        IReadOnlySet<string> allowedCommands, bool readOnly, CancellationToken ct)
    {
        var cli = cliFactory.Create(agent.Runtime);
        var credentials = new Dictionary<string, string>();
        if (agent.Runtime == AgentRuntime.ClaudeCode)
        {
            if (await secrets.GetAsync(project.Id, SecretNames.ClaudeCodeOAuthToken, ct) is { Length: > 0 } oauth) credentials["CLAUDE_CODE_OAUTH_TOKEN"] = oauth;
            else credentials["ANTHROPIC_API_KEY"] = await secrets.RequireAsync(project.Id, SecretNames.AnthropicApiKey, ct);
        }
        else
        {
            credentials["CURSOR_API_KEY"] = await secrets.RequireAsync(project.Id, SecretNames.CursorApiKey, ct);
        }
        var price = agent.Runtime == AgentRuntime.ClaudeCode ? await prices.FindAsync(run.OrganizationId, agent.Provider, agent.Model, ct) : null;
        var invocation = new CliInvocation(agent.Model, workDir, homeDir, instructions, Truncate(prompt, 100_000), allowedCommands, readOnly,
            agent.Settings.MaxIterations, Math.Max(0.01m, agent.Settings.MaxCostPerRunUsd - run.CostUsd), credentials);
        var (arguments, environment) = cli.Prepare(invocation);
        await AddStepAsync(run, RunStepType.Log, $"Started {cli.DisplayName}", $"{agent.Model} · {(readOnly ? "read-only" : "commands: " + string.Join(", ", allowedCommands.Order()))}", ct: ct);

        var startCost = run.CostUsd;
        long input = 0, output = 0, cached = 0;
        var steps = 0;
        var lastCall = System.Diagnostics.Stopwatch.GetTimestamp();
        CliResult? result = null;
        string? lastText = null;
        ProcessResult exit;
        try
        {
            exit = await processes.RunStreamingAsync(cli.Executable, arguments, workDir, environment, TimeSpan.FromMinutes(agent.Settings.TimeoutMinutes), async line =>
            {
                foreach (var e in cli.Parse(line))
                {
                    switch (e)
                    {
                        case CliModelCall call:
                            var cost = price?.Cost(call.InputTokens, call.OutputTokens, call.CachedInputTokens) ?? 0;
                            run.RecordUsage(call.InputTokens, call.OutputTokens, call.CachedInputTokens, cost);
                            (input, output, cached) = (input + call.InputTokens, output + call.OutputTokens, cached + call.CachedInputTokens);
                            var duration = (int)System.Diagnostics.Stopwatch.GetElapsedTime(lastCall).TotalMilliseconds;
                            lastCall = System.Diagnostics.Stopwatch.GetTimestamp();
                            if (steps++ < MaxCliSteps)
                                await AddStepAsync(run, RunStepType.ModelCall, $"Model call {run.ModelCalls}", null, call.InputTokens, call.OutputTokens, cost, duration, ct);
                            await EnforceLimitsAsync(run, agent, ct);
                            break;
                        case CliToolUse tool:
                            run.ToolCalls++;
                            if (steps++ < MaxCliSteps) await AddStepAsync(run, RunStepType.ToolCall, tool.Tool, tool.Detail, ct: ct);
                            break;
                        case CliText text:
                            lastText = text.Text;
                            break;
                        case CliResult final:
                            result = final;
                            break;
                    }
                }
            }, ct);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"{cli.DisplayName} is not installed on this worker ('{cli.Executable}'). Install it or set AgentRuntime:{(agent.Runtime == AgentRuntime.ClaudeCode ? "ClaudeCliPath" : "CursorCliPath")}.");
        }

        if (result is null)
            throw new InvalidOperationException(exit.TimedOut
                ? $"{cli.DisplayName} timed out after {agent.Settings.TimeoutMinutes} minutes"
                : $"{cli.DisplayName} exited with code {exit.ExitCode} without a result: {Truncate(exit.Output.Trim(), 1500)}");
        // Totals from the final event cover requests the stream did not itemize.
        var extra = (In: Math.Max(0, (result.InputTokens ?? 0) - input), Out: Math.Max(0, (result.OutputTokens ?? 0) - output), Cached: Math.Max(0, (result.CachedInputTokens ?? 0) - cached));
        var extraCost = price is not null ? price.Cost(extra.In, extra.Out, extra.Cached)
            : result.CostUsd is { } reported ? Math.Max(0, reported - (run.CostUsd - startCost)) : 0;
        if (extra.In + extra.Out > 0 || extraCost > 0) run.AddUsage(extra.In, extra.Out, extra.Cached, extraCost);
        if (agent.Runtime == AgentRuntime.CursorCli && input + output + (result.InputTokens ?? 0) == 0)
            await AddStepAsync(run, RunStepType.Log, "Usage not reported", "Cursor CLI does not report tokens; the cost is billed by the Cursor plan and is not in this run's total.", ct: ct);
        await EnforceLimitsAsync(run, agent, ct);
        if (result.IsError) throw new InvalidOperationException($"{cli.DisplayName}: {Truncate(result.Text, 2000)}");
        if (exit.ExitCode != 0) throw new InvalidOperationException($"{cli.DisplayName} exited with code {exit.ExitCode}: {Truncate(exit.Output.Trim(), 1500)}");
        return result.Text.Length > 0 ? result.Text : lastText ?? "";
    }

    private async Task EnforceLimitsAsync(AgentRun run, Agent agent, CancellationToken ct)
    {
        if (run.InputTokens + run.OutputTokens > agent.Settings.MaxTokensPerRun)
            throw new RunLimitExceededException($"Token limit exceeded ({run.InputTokens + run.OutputTokens} > {agent.Settings.MaxTokensPerRun})");
        if (run.CostUsd > agent.Settings.MaxCostPerRunUsd)
            throw new RunLimitExceededException($"Cost limit exceeded ({run.CostUsd:0.0000} > {agent.Settings.MaxCostPerRunUsd} USD)");
        if (await budgets.ExhaustedAsync(agent, run.ProjectId, ct) is { } exhausted) throw new RunLimitExceededException(exhausted);
    }

    /// <summary>Hash of a clone's git config and hooks; the platform runs git there after the agent, so these must not change.</summary>
    public static string GitMetadataFingerprint(string repository)
    {
        var git = Path.Combine(repository, ".git");
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var file in new[] { Path.Combine(git, "config"), Path.Combine(git, "info", "attributes") }
                     .Concat(Directory.Exists(Path.Combine(git, "hooks")) ? Directory.EnumerateFiles(Path.Combine(git, "hooks"), "*", SearchOption.AllDirectories).Order() : []))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(file));
            if (File.Exists(file)) sha.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static string Instructions(Agent agent, IReadOnlyList<Repository> repositories)
    {
        var builder = new StringBuilder(agent.Instructions.Trim()).AppendLine().AppendLine();
        builder.AppendLine(agent.Kind == AgentKind.Developer
            ? agent.Runtime == AgentRuntime.Framework
                ? """
                  You are a software engineer working for the team on Azure DevOps. Use the tools to explore the code, make the change, and run the
                  repository's build and tests with run_command. Do not use git: the platform commits, pushes and opens the pull request.
                  Finish with a concise summary of what you changed and how you verified it.
                  """
                : """
                  You are a software engineer working for the team on Azure DevOps. The repositories are folders of the current directory with the
                  work branch checked out. Explore the code, make the change, and run the repository's build and tests with the allowed commands.
                  Do not use git and do not touch .git: the platform commits, pushes and opens the pull request. You cannot ask questions; decide
                  and note assumptions. Finish with a concise summary of what you changed and how you verified it.
                  """
            : "You are a meticulous code reviewer. Base every finding on the code shown; cite file paths and line numbers.");
        foreach (var repo in repositories)
            builder.AppendLine($"Repository {repo.Name}: allowed commands {string.Join(", ", repo.Policy.AllowedCommands)}; at most {repo.Policy.MaxFilesChangedPerRun} changed files.");
        foreach (var skill in agent.Skills.Where(s => s.Enabled)) builder.AppendLine().AppendLine($"## Skill: {skill.Name}").AppendLine(skill.Content.Trim());
        return builder.ToString();
    }

    private async Task AddStepAsync(AgentRun run, RunStepType type, string title, string? detail, long input = 0, long output = 0, decimal cost = 0,
        int durationMs = 0, CancellationToken ct = default)
    {
        if (_sequence == 0) _sequence = await db.AgentRunSteps.Where(s => s.RunId == run.Id).CountAsync(ct);
        db.AgentRunSteps.Add(new AgentRunStep
        {
            OrganizationId = run.OrganizationId, RunId = run.Id, Sequence = ++_sequence, Type = type, Title = title,
            Detail = detail is null ? null : Truncate(detail, 8000), InputTokens = input, OutputTokens = output, CostUsd = cost, DurationMs = durationMs,
        });
        await db.SaveChangesAsync(ct);
    }

    private static bool BranchMatches(string pattern, string branch) =>
        pattern.EndsWith("/*", StringComparison.Ordinal) ? branch.StartsWith(pattern[..^1], StringComparison.Ordinal) : pattern == branch;

    private static string SafeName(string name) => Regex.Replace(name, "[^A-Za-z0-9_-]", "_");

    private static string Truncate(string text, int max) => text.Length > max ? text[..max] : text;

    private void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException e) { logger.LogWarning(e, "Could not delete workspace {Dir}", dir); }
    }

    [GeneratedRegex(@"\{[\s\S]*\}")]
    private static partial Regex JsonObject();
}
