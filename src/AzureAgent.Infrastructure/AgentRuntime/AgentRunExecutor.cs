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

public sealed class AgentRuntimeOptions
{
    public const string Section = "AgentRuntime";
    public string WorkspaceRoot { get; set; } = Path.Combine(Path.GetTempPath(), "azure-agent-workspaces");
    public bool KeepWorkspaces { get; set; }
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
    McpToolLoader mcpTools,
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
        var agent = await db.Agents.Include(a => a.Skills).Include(a => a.McpServers).FirstAsync(a => a.Id == run.AgentId, ct);
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["RunId"] = run.Id, ["AgentId"] = agent.Id, ["ProjectId"] = run.ProjectId });

        if (!agent.Enabled) { await FailAsync(run, "Agent is disabled", ct); return; }
        if (await budgets.ExhaustedAsync(agent, ct) is { } exhausted) { await FailAsync(run, exhausted, ct); return; }
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
        try
        {
            if (run.Kind == RunKind.Implement) await ImplementAsync(run, agent, runDir, timeout.Token);
            else await ReviewAsync(run, agent, timeout.Token);
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

    private async Task ImplementAsync(AgentRun run, Agent agent, string runDir, CancellationToken ct)
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
            if (!await PairedDeveloperMayWriteAsync(project, agent, repo, ct))
                throw new InvalidOperationException($"{agent.PairedUniqueName} does not have '{Permission.RepositoryAgentWrite}' on {repo.Name}");
        }

        var pat = await secrets.RequireAsync(project.Id, SecretNames.AzureDevOpsPat, ct);
        var git = new GitWorkspace(processes, project.AzureOrganizationUrl, pat, agent.Name, $"agent-{agent.Id:N}@azure-agent.local");
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
        var tools = new WorkspaceTools(runDir, allowed, processes, (tool, detail) => run.ToolCalls++).Create();
        var prompt = ImplementationPrompt(item, repositories, branch);
        var summary = await RunAgentAsync(run, agent, project, Instructions(agent, await SkillsAsync(agent, ct), repositories), tools, prompt, ct);
        run.Summary = summary;

        var (azureProject, azure) = await connector.ConnectAsync(project.Id, ct);
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
        if (azureProject.Settings.ReviewState is { Length: > 0 } reviewState)
            await azure.UpdateWorkItemFieldsAsync(item.AzureId, new Dictionary<string, object?> { ["System.State"] = reviewState }, ct);
    }

    private async Task<bool> PairedDeveloperMayWriteAsync(Project project, Agent agent, Repository repo, CancellationToken ct)
    {
        // The agent acts for its paired developer: their project role and repository rules apply.
        if (agent.PairedUniqueName is null) return true;
        var member = await db.ProjectMembers.AsNoTracking().FirstOrDefaultAsync(m => m.ProjectId == project.Id && m.AzureUniqueName == agent.PairedUniqueName, ct);
        if (member is null) return true;
        var permissions = RolePermissions.For(member.Role).ToHashSet();
        var rules = await db.RepositoryAccessRules.AsNoTracking()
            .Where(r => r.RepositoryId == repo.Id && r.Permission == Permission.RepositoryAgentWrite && (r.UserId == member.UserId || r.Role == member.Role)).ToListAsync(ct);
        if (rules.Any(r => r.Effect == AccessEffect.Allow)) permissions.Add(Permission.RepositoryAgentWrite);
        if (rules.Any(r => r.Effect == AccessEffect.Deny)) permissions.Remove(Permission.RepositoryAgentWrite);
        return permissions.Contains(Permission.RepositoryAgentWrite);
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

    private async Task ReviewAsync(AgentRun run, Agent agent, CancellationToken ct)
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

        var text = await RunAgentAsync(run, agent, project, Instructions(agent, await SkillsAsync(agent, ct), [repo]), [], builder.ToString(), ct);
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

        await using var mcp = await mcpTools.LoadAsync(project.Id, agent, ct);
        var allTools = tools.Concat(mcp.Tools).ToList();

        var metered = new UsageMeteringChatClient(chatClients.Create(agent.Provider, agent.Model, apiKey, agent.Settings.Endpoint), async (usage, token) =>
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
            if (await budgets.ExhaustedAsync(agent, token) is { } exhausted) throw new RunLimitExceededException(exhausted);
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

    private static string Instructions(Agent agent, IReadOnlyList<Skill> skills, IReadOnlyList<Repository> repositories)
    {
        var builder = new StringBuilder(agent.Instructions.Trim()).AppendLine().AppendLine();
        builder.AppendLine(agent.Kind == AgentKind.Developer
            ? """
              You are a software engineer working for the team on Azure DevOps. Use the tools to explore the code, make the change, and run the
              repository's build and tests with run_command. Do not use git: the platform commits, pushes and opens the pull request.
              Finish with a concise summary of what you changed and how you verified it.
              """
            : "You are a meticulous code reviewer. Base every finding on the code shown; cite file paths and line numbers.");
        foreach (var repo in repositories)
            builder.AppendLine($"Repository {repo.Name}: allowed commands {string.Join(", ", repo.Policy.AllowedCommands)}; at most {repo.Policy.MaxFilesChangedPerRun} changed files.");
        foreach (var skill in skills) builder.AppendLine().AppendLine($"## Skill: {skill.Name}").AppendLine(skill.Content.Trim());
        return builder.ToString();
    }

    private async Task<IReadOnlyList<Skill>> SkillsAsync(Agent agent, CancellationToken ct)
    {
        var ids = agent.Skills.Select(s => s.SkillId).ToList();
        return await db.Skills.AsNoTracking().Where(s => ids.Contains(s.Id)).OrderBy(s => s.Key).ToListAsync(ct);
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
