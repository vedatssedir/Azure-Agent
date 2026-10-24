using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Application.Features.Secrets;
using AzureAgent.Domain.Agents;
using AzureAgent.Domain.Azure;
using AzureAgent.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzureAgent.Application.Features.Azure;

public sealed class AzureConnector(IAppDbContext db, SecretResolver secrets, IAzureDevOpsClientFactory factory)
{
    public async Task<(Project Project, IAzureDevOpsClient Client)> ConnectAsync(Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct) ?? throw new NotFoundException("Project");
        var pat = await secrets.RequireAsync(projectId, SecretNames.AzureDevOpsPat, ct);
        return (project, factory.Create(new AzureConnectionInfo(project.AzureOrganizationUrl, project.AzureProjectName, pat)));
    }
}

/// <summary>Creates runs for assigned work and for pull requests that need review.</summary>
public sealed class Dispatcher(IAppDbContext db, ILogger<Dispatcher> logger)
{
    public async Task<List<ExecuteAgentRun>> QueueAssignedWorkAsync(Project project, CancellationToken ct)
    {
        var messages = new List<ExecuteAgentRun>();
        if (!project.Settings.AutoDispatch) return messages;
        var closed = project.Settings.ClosedStates;
        var candidates = await db.WorkItems
            .Where(w => w.ProjectId == project.Id && w.AssignedToUniqueName != null && !closed.Contains(w.State))
            .Join(db.Agents.Where(a => a.ProjectId == project.Id && a.Enabled && a.Kind == AgentKind.Developer), w => w.AssignedToUniqueName, a => a.PairedUniqueName, (w, a) => new { w, a })
            .Where(x => !db.AgentRuns.Any(r => r.WorkItemId == x.w.Id && r.Kind == RunKind.Implement
                && (r.Status == RunStatus.PendingApproval || r.Status == RunStatus.Queued || r.Status == RunStatus.Running || r.Status == RunStatus.Succeeded)))
            .ToListAsync(ct);
        foreach (var x in candidates)
        {
            var run = AgentRun.Create(project.OrganizationId, project.Id, x.a.Id, RunKind.Implement, project.Settings.RequireRunApproval,
                x.w.Id, x.w.AzureId, requestedBy: $"assignment:{x.w.AssignedToUniqueName}");
            db.AgentRuns.Add(run);
            db.AuditEntries.Add(new Domain.Auditing.AuditEntry
            {
                OrganizationId = project.OrganizationId, ProjectId = project.Id, Actor = "dispatcher", Action = "run.queued",
                EntityType = "work_item", EntityId = x.w.AzureId.ToString(), Details = $"{{\"agent\":\"{x.a.Name}\"}}",
            });
            if (run.Status == RunStatus.Queued) messages.Add(new ExecuteAgentRun(project.OrganizationId, run.Id));
            logger.LogInformation("Queued {Kind} run {RunId} for work item {WorkItem} on agent {Agent}", run.Kind, run.Id, x.w.AzureId, x.a.Name);
        }
        return messages;
    }

    public async Task<List<ExecuteAgentRun>> QueueReviewsAsync(Project project, IReadOnlyCollection<PullRequest> pullRequests, CancellationToken ct)
    {
        var messages = new List<ExecuteAgentRun>();
        if (!project.Settings.AutoReviewPullRequests || pullRequests.Count == 0) return messages;
        var reviewer = await db.Agents.FirstOrDefaultAsync(a => a.ProjectId == project.Id && a.Enabled && a.Kind == AgentKind.Reviewer, ct);
        if (reviewer is null) return messages;
        var repoIds = pullRequests.Select(p => p.RepositoryId).ToHashSet();
        var autoReviewRepos = (await db.Repositories.Where(r => repoIds.Contains(r.Id)).ToListAsync(ct))
            .Where(r => r.Policy.AutoReview).Select(r => r.Id).ToHashSet();
        foreach (var pr in pullRequests.Where(p => p.Status == PullRequestStatus.Active && autoReviewRepos.Contains(p.RepositoryId)))
        {
            if (await db.AgentRuns.AnyAsync(r => r.PullRequestId == pr.Id && r.Kind == RunKind.Review
                && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running || r.Status == RunStatus.PendingApproval), ct)) continue;
            var run = AgentRun.Create(project.OrganizationId, project.Id, reviewer.Id, RunKind.Review, false, pullRequestId: pr.Id, requestedBy: "auto-review");
            db.AgentRuns.Add(run);
            messages.Add(new ExecuteAgentRun(project.OrganizationId, run.Id));
        }
        return messages;
    }
}

public sealed class ProjectSynchronizer(IAppDbContext db, AzureConnector connector, Dispatcher dispatcher, IClock clock, ILogger<ProjectSynchronizer> logger)
{
    public async Task<(SyncResult Result, List<ExecuteAgentRun> Messages)> SyncAsync(Guid projectId, CancellationToken ct)
    {
        var (project, azure) = await connector.ConnectAsync(projectId, ct);
        var startedAt = clock.UtcNow;
        try
        {
            var teams = await SyncTeamsAsync(project, azure, ct);
            var (repositories, commits, pullRequests, changedPullRequests) = await SyncRepositoriesAsync(project, azure, ct);
            var ids = await azure.QueryWorkItemIdsAsync(WorkItemMapping.ReconciliationQuery(project.LastSyncedAt, project.Settings.ClosedStates), ct);
            var workItems = await UpsertWorkItemsAsync(project, azure, await azure.GetWorkItemsAsync(ids, ct), ct);
            await db.SaveChangesAsync(ct);

            var messages = await dispatcher.QueueAssignedWorkAsync(project, ct);
            messages.AddRange(await dispatcher.QueueReviewsAsync(project, changedPullRequests, ct));
            project.LastSyncedAt = startedAt;
            project.LastSyncError = null;
            await db.SaveChangesAsync(ct);
            var result = new SyncResult(teams, repositories, workItems, commits, pullRequests, messages.Count);
            logger.LogInformation("Synced project {Project}: {@Result}", project.Key, result);
            return (result, messages);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Sync failed for project {Project}", project.Key);
            project.LastSyncError = e.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<List<ExecuteAgentRun>> SyncWorkItemAsync(Guid projectId, int azureId, CancellationToken ct)
    {
        var (project, azure) = await connector.ConnectAsync(projectId, ct);
        await UpsertWorkItemsAsync(project, azure, await azure.GetWorkItemsAsync([azureId], ct), ct);
        await db.SaveChangesAsync(ct);
        var messages = await dispatcher.QueueAssignedWorkAsync(project, ct);
        await db.SaveChangesAsync(ct);
        return messages;
    }

    private async Task<int> SyncTeamsAsync(Project project, IAzureDevOpsClient azure, CancellationToken ct)
    {
        var teams = await azure.ListTeamsAsync(ct);
        var existing = await db.Teams.Where(t => t.ProjectId == project.Id).ToDictionaryAsync(t => t.AzureId, ct);
        foreach (var team in teams)
        {
            var members = (await azure.ListTeamMembersAsync(team.Id, ct))
                .Select(m => new TeamMember { UniqueName = m.UniqueName.ToLowerInvariant(), DisplayName = m.DisplayName }).ToList();
            if (!existing.TryGetValue(team.Id, out var row))
            {
                row = new Team { OrganizationId = project.OrganizationId, ProjectId = project.Id, AzureId = team.Id, Name = team.Name };
                db.Teams.Add(row);
            }
            row.Name = team.Name;
            row.Members = members;
        }
        return teams.Count;
    }

    private async Task<(int Repositories, int Commits, int PullRequests, List<PullRequest> Changed)> SyncRepositoriesAsync(
        Project project, IAzureDevOpsClient azure, CancellationToken ct)
    {
        var repos = await azure.ListRepositoriesAsync(ct);
        var existing = await db.Repositories.Where(r => r.ProjectId == project.Id).ToDictionaryAsync(r => r.AzureId, ct);
        var commitsSince = project.LastSyncedAt?.AddHours(-1) ?? clock.UtcNow.AddDays(-30);
        int commitCount = 0, prCount = 0;
        var changed = new List<PullRequest>();
        foreach (var repo in repos)
        {
            if (!existing.TryGetValue(repo.Id, out var row))
            {
                row = new Repository
                {
                    OrganizationId = project.OrganizationId, ProjectId = project.Id, AzureId = repo.Id, Name = repo.Name,
                    RemoteUrl = repo.RemoteUrl, WebUrl = repo.WebUrl,
                };
                db.Repositories.Add(row);
            }
            row.Name = repo.Name;
            row.DefaultBranch = repo.DefaultBranch;
            row.RemoteUrl = repo.RemoteUrl;
            row.WebUrl = repo.WebUrl;
            // Empty repositories have no default branch and reject commit queries.
            if (repo.DefaultBranch is null) continue;

            var knownCommits = (await db.Commits.Where(c => c.RepositoryId == row.Id && c.CommittedAt >= commitsSince.AddDays(-1))
                .Select(c => c.CommitId).ToListAsync(ct)).ToHashSet();
            foreach (var commit in await azure.ListCommitsAsync(repo.Name, commitsSince, ct))
            {
                if (!knownCommits.Add(commit.CommitId)) continue;
                db.Commits.Add(new Commit
                {
                    OrganizationId = project.OrganizationId, RepositoryId = row.Id, CommitId = commit.CommitId, AuthorName = commit.AuthorName,
                    AuthorEmail = commit.AuthorEmail, Message = commit.Comment, CommittedAt = commit.Date,
                    WebUrl = $"{repo.WebUrl}/commit/{commit.CommitId}", WorkItemAzureIds = WorkItemMapping.WorkItemReferences(commit.Comment),
                });
                commitCount++;
            }

            var knownPrs = await db.PullRequests.Where(p => p.RepositoryId == row.Id).ToDictionaryAsync(p => p.AzureId, ct);
            foreach (var pr in await azure.ListPullRequestsAsync(repo.Name, ct))
            {
                var status = pr.Status switch { "completed" => PullRequestStatus.Completed, "abandoned" => PullRequestStatus.Abandoned, _ => PullRequestStatus.Active };
                if (knownPrs.TryGetValue(pr.Id, out var known) && known.Status == status && known.Title == pr.Title) continue;
                if (known is null)
                {
                    known = new PullRequest
                    {
                        OrganizationId = project.OrganizationId, RepositoryId = row.Id, AzureId = pr.Id, Title = pr.Title,
                        SourceBranch = pr.SourceRefName.Replace("refs/heads/", ""), TargetBranch = pr.TargetRefName.Replace("refs/heads/", ""),
                        CreatedBy = pr.CreatedBy, CreatedByAgent = pr.SourceRefName.StartsWith("refs/heads/ab/", StringComparison.Ordinal),
                        OpenedAt = pr.CreationDate, WebUrl = azure.PullRequestWebUrl(repo.WebUrl, pr.Id),
                    };
                    db.PullRequests.Add(known);
                }
                known.Title = pr.Title;
                known.Status = status;
                known.ClosedAt = pr.ClosedDate;
                known.WorkItemAzureIds = (await azure.GetPullRequestWorkItemIdsAsync(repo.Name, pr.Id, ct)).ToList();
                changed.Add(known);
                prCount++;
            }
        }
        return (repos.Count, commitCount, prCount, changed);
    }

    private async Task<int> UpsertWorkItemsAsync(Project project, IAzureDevOpsClient azure, IReadOnlyList<AzureWorkItem> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.Id).ToList();
        var existing = await db.WorkItems.Where(w => w.ProjectId == project.Id && ids.Contains(w.AzureId)).ToDictionaryAsync(w => w.AzureId, ct);
        var changed = 0;
        foreach (var item in items)
        {
            if (existing.TryGetValue(item.Id, out var row) && row.Revision >= item.Rev) continue;
            if (row is null)
            {
                row = new WorkItem
                {
                    OrganizationId = project.OrganizationId, ProjectId = project.Id, AzureId = item.Id, Type = "", Title = "", State = "",
                    WebUrl = azure.WorkItemWebUrl(item.Id),
                };
                db.WorkItems.Add(row);
            }
            WorkItemMapping.Apply(row, item);
            changed++;
        }
        return changed;
    }
}

// Handlers ------------------------------------------------------------------------------------

public static class SyncProjectHandler
{
    /// <summary>Returned messages cascade through Wolverine (queued runs go to the agent-runs queue).</summary>
    public static async Task<(SyncResult, IEnumerable<object>)> Handle(SyncProject message, ITenantContext tenant, ProjectSynchronizer synchronizer, CancellationToken ct)
    {
        tenant.Use(message.OrganizationId);
        var (result, runs) = await synchronizer.SyncAsync(message.ProjectId, ct);
        return (result, runs);
    }
}

public static class SyncWorkItemHandler
{
    public static async Task<IEnumerable<object>> Handle(SyncWorkItem message, ITenantContext tenant, ProjectSynchronizer synchronizer, CancellationToken ct)
    {
        tenant.Use(message.OrganizationId);
        return await synchronizer.SyncWorkItemAsync(message.ProjectId, message.WorkItemAzureId, ct);
    }
}

/// <summary>User-triggered sync (HTTP). Requires settings permission.</summary>
public sealed record RequestProjectSync(Guid ProjectId);

public static class RequestProjectSyncHandler
{
    public static async Task<(SyncResult, IEnumerable<object>)> Handle(RequestProjectSync command, AccessControl access, ProjectSynchronizer synchronizer, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.SettingsManage, ct);
        var (result, runs) = await synchronizer.SyncAsync(command.ProjectId, ct);
        return (result, runs);
    }
}

public sealed record TestAzureConnection(Guid ProjectId);

public sealed record ConnectionCheck(string Name, bool Ok, string Detail);

public static class TestAzureConnectionHandler
{
    public static async Task<IReadOnlyList<ConnectionCheck>> Handle(TestAzureConnection command, AccessControl access, AzureConnector connector, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.SettingsManage, ct);
        var checks = new List<ConnectionCheck>();
        try
        {
            var (_, azure) = await connector.ConnectAsync(command.ProjectId, ct);
            checks.Add(new("project", true, await azure.GetProjectNameAsync(ct)));
            checks.Add(new("teams", true, string.Join(", ", (await azure.ListTeamsAsync(ct)).Select(t => t.Name))));
            checks.Add(new("repositories", true, string.Join(", ", (await azure.ListRepositoriesAsync(ct)).Select(r => r.Name))));
        }
        catch (Exception e) when (e is not AppException { StatusCode: 403 or 404 })
        {
            checks.Add(new("azure", false, e.Message));
        }
        return checks;
    }
}
