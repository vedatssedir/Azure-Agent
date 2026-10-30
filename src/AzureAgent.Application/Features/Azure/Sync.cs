using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Application.Features.Identity;
using AzureAgent.Domain.Agents;
using AzureAgent.Domain.Azure;
using AzureAgent.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzureAgent.Application.Features.Azure;

/// <summary>
/// Creates runs. A work item goes to the agent named by its <c>agent:&lt;slug&gt;</c> tag, otherwise
/// (with auto dispatch) to the default agent of the developer it is assigned to. New pull requests in
/// repositories with auto review go to the organization's reviewer agent.
/// </summary>
public sealed class Dispatcher(IAppDbContext db, ILogger<Dispatcher> logger)
{
    public async Task<List<ExecuteAgentRun>> QueueAssignedWorkAsync(Project project, CancellationToken ct)
    {
        var messages = new List<ExecuteAgentRun>();
        var settings = await db.Organizations.AsNoTracking().Where(o => o.Id == project.OrganizationId).Select(o => o.Settings).FirstAsync(ct);
        var agents = await db.Agents.Where(a => a.Enabled && a.Kind == AgentKind.Developer && (a.ProjectId == null || a.ProjectId == project.Id)).ToListAsync(ct);
        if (agents.Count == 0) return messages;
        var ownerIds = agents.Where(a => a.OwnerUserId != null).Select(a => a.OwnerUserId!.Value).Distinct().ToList();
        var owners = await db.Users.AsNoTracking().Where(u => ownerIds.Contains(u.Id) && u.IsActive).ToDictionaryAsync(u => u.Email, u => u.Id, ct);
        var defaults = agents.Where(a => a.IsDefault && a.OwnerUserId != null).GroupBy(a => a.OwnerUserId!.Value).ToDictionary(g => g.Key, g => g.First());

        var candidates = await db.WorkItems
            .Where(w => w.ProjectId == project.Id && (w.AssignedToUniqueName != null || w.Tags.Count > 0))
            // Automatic dispatch happens once per work item; later runs are started by people (Run agent / Retry).
            .Where(w => !db.AgentRuns.Any(r => r.WorkItemId == w.Id && r.Kind == RunKind.Implement))
            .ToListAsync(ct);
        foreach (var item in candidates.Where(w => !project.IsClosedState(w.Type, w.State)))
        {
            Agent? agent = null;
            string? requestedBy = null;
            if (WorkItemMapping.AgentTag(item.Tags) is { } slug)
            {
                agent = agents.FirstOrDefault(a => a.Slug == slug);
                requestedBy = $"tag:agent:{slug}";
            }
            else if (settings.AutoDispatch && item.AssignedToUniqueName is { } email && owners.TryGetValue(email, out var ownerId))
            {
                agent = defaults.GetValueOrDefault(ownerId);
                requestedBy = $"assignment:{email}";
            }
            if (agent is null) continue;
            var run = AgentRun.Create(project.OrganizationId, project.Id, agent.Id, RunKind.Implement, settings.RequireRunApproval, item.Id, item.AzureId,
                requestedBy: requestedBy);
            db.AgentRuns.Add(run);
            db.AuditEntries.Add(new Domain.Auditing.AuditEntry
            {
                OrganizationId = project.OrganizationId, ProjectId = project.Id, Actor = "dispatcher", Action = "run.queued",
                EntityType = "work_item", EntityId = item.AzureId.ToString(), Details = System.Text.Json.JsonSerializer.Serialize(new { agent = agent.Name, requestedBy }),
            });
            if (run.Status == RunStatus.Queued) messages.Add(new ExecuteAgentRun(project.OrganizationId, run.Id));
            logger.LogInformation("Queued {Kind} run {RunId} for work item {WorkItem} on agent {Agent} ({RequestedBy})", run.Kind, run.Id, item.AzureId, agent.Name, requestedBy);
        }
        return messages;
    }

    public async Task<List<ExecuteAgentRun>> QueueReviewsAsync(Project project, IReadOnlyCollection<PullRequest> pullRequests, CancellationToken ct)
    {
        var messages = new List<ExecuteAgentRun>();
        var settings = await db.Organizations.AsNoTracking().Where(o => o.Id == project.OrganizationId).Select(o => o.Settings).FirstAsync(ct);
        if (!settings.AutoReviewPullRequests || pullRequests.Count == 0) return messages;
        var reviewer = await db.Agents.Where(a => a.Enabled && a.Kind == AgentKind.Reviewer && (a.ProjectId == null || a.ProjectId == project.Id))
            .OrderByDescending(a => a.ProjectId != null).ThenBy(a => a.CreatedAt).FirstOrDefaultAsync(ct);
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

/// <summary>Mirrors the organization's project list: new projects appear, renamed ones follow, deleted ones are retired.</summary>
public sealed class OrganizationSynchronizer(IAppDbContext db, AzureConnector connector, IClock clock, ILogger<OrganizationSynchronizer> logger)
{
    public async Task<(OrganizationSyncResult Result, List<SyncProject> Projects)> SyncAsync(Guid organizationId, string trigger, CancellationToken ct)
    {
        var organization = await connector.OrganizationAsync(organizationId, ct);
        try
        {
            var azure = await connector.ConnectOrganizationAsync(organizationId, ct);
            var remote = await azure.ListProjectsAsync(ct);
            var existing = await db.Projects.ToListAsync(ct);
            var keys = existing.Select(p => p.Key).ToHashSet();
            var added = 0;
            foreach (var project in remote)
            {
                var row = existing.FirstOrDefault(p => p.AzureId == project.Id);
                if (row is null)
                {
                    row = new Project
                    {
                        OrganizationId = organizationId, AzureId = project.Id, Name = project.Name, Key = UniqueKey(project.Name, keys),
                        AzureOrganizationUrl = organization.AzureOrganizationUrl!, AzureProjectName = project.Name,
                    };
                    db.Projects.Add(row);
                    existing.Add(row);
                    added++;
                }
                row.Name = project.Name;
                row.AzureProjectName = project.Name;
                row.Description = project.Description;
                row.AzureOrganizationUrl = organization.AzureOrganizationUrl!;
                row.IsRemoved = false;
            }
            var removed = 0;
            foreach (var gone in existing.Where(p => !p.IsRemoved && remote.All(r => r.Id != p.AzureId)))
            {
                gone.IsRemoved = true;
                removed++;
            }
            organization.LastSyncedAt = clock.UtcNow;
            organization.LastSyncError = null;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Organization {Organization} has {Count} projects ({Added} new, {Removed} removed)", organization.Name, remote.Count, added, removed);
            var sync = existing.Where(p => !p.IsRemoved).Select(p => new SyncProject(organizationId, p.Id, trigger)).ToList();
            return (new OrganizationSyncResult(remote.Count, added, removed), sync);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Organization sync failed for {Organization}", organization.Name);
            organization.LastSyncError = e.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>Uppercase key from the project name (letters and digits, 2-10 chars), unique in the organization.</summary>
    public static string UniqueKey(string name, ISet<string> taken)
    {
        var letters = new string(Slugs.From(name, 60).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        var key = letters.Length >= 2 ? letters[..Math.Min(8, letters.Length)] : "PRJ";
        if (!char.IsLetter(key[0])) key = "P" + key[..Math.Min(7, key.Length)];
        var candidate = key;
        for (var i = 2; taken.Contains(candidate); i++) candidate = key[..Math.Min(key.Length, 8)] + i;
        taken.Add(candidate);
        return candidate;
    }
}

/// <summary>
/// Full reconciliation of one project: process (types, states), teams (members, areas, boards,
/// sprints), repositories (git mirror with all history, AB# commits, pull requests), work items and
/// memberships; then dispatch.
/// </summary>
public sealed class ProjectSynchronizer(IAppDbContext db, AzureConnector connector, Dispatcher dispatcher, MembershipProvisioner provisioner,
    IRepositoryMirror mirror, IClock clock, ILogger<ProjectSynchronizer> logger)
{
    public async Task<(SyncResult Result, List<ExecuteAgentRun> Messages)> SyncAsync(Guid projectId, CancellationToken ct)
    {
        var (project, azure) = await connector.ConnectAsync(projectId, ct);
        var startedAt = clock.UtcNow;
        try
        {
            project.WorkItemTypes = (await azure.ListWorkItemTypesAsync(ct)).Select(t => new WorkItemTypeInfo
            {
                Name = t.Name, Color = t.Color, IsDisabled = t.IsDisabled, Fields = t.Fields.ToList(),
                States = t.States.Select(s => new WorkItemStateInfo { Name = s.Name, Color = s.Color, Category = s.Category }).ToList(),
            }).ToList();
            var teams = await SyncTeamsAsync(project, azure, ct);
            await db.SaveChangesAsync(ct);
            var (repositories, commits, pullRequests, changedPullRequests) = await SyncRepositoriesAsync(project, azure, ct);
            var closed = project.WorkItemTypes.SelectMany(t => t.States).Where(s => s.Category is "Completed" or "Removed").Select(s => s.Name).Distinct().ToList();
            var ids = await azure.QueryWorkItemIdsAsync(WorkItemMapping.ReconciliationQuery(project.LastSyncedAt, closed), ct);
            var workItems = await UpsertWorkItemsAsync(project, azure, await azure.GetWorkItemsAsync(ids, ct), ct);
            await db.SaveChangesAsync(ct);
            var members = await provisioner.SyncProjectAsync(project, "sync", ct);

            var messages = await dispatcher.QueueAssignedWorkAsync(project, ct);
            messages.AddRange(await dispatcher.QueueReviewsAsync(project, changedPullRequests, ct));
            project.LastSyncedAt = startedAt;
            project.LastSyncError = null;
            await db.SaveChangesAsync(ct);
            var result = new SyncResult(teams, repositories, workItems, commits, pullRequests, messages.Count, members.Added, members.Removed, members.Invited);
            logger.LogInformation("Synced project {Project}: {@Result}", project.Key, result);
            return (result, messages);
        }
        catch (Exception e) when (e is not OperationCanceledException)
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
            if (!existing.TryGetValue(team.Id, out var row))
            {
                row = new Team { OrganizationId = project.OrganizationId, ProjectId = project.Id, AzureId = team.Id, Name = team.Name };
                db.Teams.Add(row);
            }
            row.Name = team.Name;
            row.Members = (await azure.ListTeamMembersAsync(team.Id, ct))
                .Select(m => new TeamMember { UniqueName = m.UniqueName.ToLowerInvariant(), DisplayName = m.DisplayName }).ToList();
            row.Areas = (await azure.ListTeamAreasAsync(team.Id, ct)).Select(a => new TeamArea { Path = a.Path, IncludeChildren = a.IncludeChildren }).ToList();
            row.Boards = (await azure.ListBoardsAsync(team.Id, ct)).Select(b => new TeamBoard
            {
                Name = b.Name, ColumnField = b.ColumnField,
                Columns = b.Columns.Select(c => new BoardColumn
                {
                    Name = c.Name, ColumnType = c.ColumnType, ItemLimit = c.ItemLimit,
                    StateMappings = c.StateMappings.Select(m => new BoardStateMapping { WorkItemType = m.Key, State = m.Value }).ToList(),
                }).ToList(),
            }).ToList();
            row.Iterations = (await azure.ListIterationsAsync(team.Id, ct)).Select(i => new TeamIteration
            {
                AzureId = i.Id, Name = i.Name, Path = i.Path, StartDate = i.StartDate, FinishDate = i.FinishDate, TimeFrame = i.TimeFrame,
            }).ToList();
        }
        foreach (var gone in existing.Values.Where(t => teams.All(x => x.Id != t.AzureId))) db.Teams.Remove(gone);
        return teams.Count;
    }

    private async Task<(int Repositories, int Commits, int PullRequests, List<PullRequest> Changed)> SyncRepositoriesAsync(
        Project project, IAzureDevOpsClient azure, CancellationToken ct)
    {
        var repos = await azure.ListRepositoriesAsync(ct);
        var existing = await db.Repositories.Where(r => r.ProjectId == project.Id).ToDictionaryAsync(r => r.AzureId, ct);
        var credential = await connector.ServiceCredentialAsync(project.OrganizationId, ct);
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
            row.SizeBytes = repo.Size;
            row.IsRemoved = repo.IsDisabled;
            await db.SaveChangesAsync(ct);
            // Empty repositories have no default branch: nothing to mirror or query.
            if (repo.DefaultBranch is null || repo.IsDisabled) continue;

            try
            {
                var firstMirror = !mirror.Exists(project.OrganizationId, row.Id);
                await mirror.SyncAsync(project.OrganizationId, row.Id, repo.RemoteUrl, project.AzureOrganizationUrl, credential, ct);
                row.MirrorSyncedAt = clock.UtcNow;
                row.MirrorError = null;
                commitCount += await LinkCommitsAsync(project, row, firstMirror ? clock.UtcNow.AddDays(-365) : (project.LastSyncedAt ?? clock.UtcNow).AddHours(-1), ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Mirroring {Repository} failed", repo.Name);
                row.MirrorError = e.Message;
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
        foreach (var gone in existing.Values.Where(r => repos.All(x => x.Id != r.AzureId))) gone.IsRemoved = true;
        return (repos.Count, commitCount, prCount, changed);
    }

    /// <summary>Stores the commits that reference work items (AB#123) so work items show them; the full history stays in the mirror.</summary>
    private async Task<int> LinkCommitsAsync(Project project, Repository repo, DateTimeOffset since, CancellationToken ct)
    {
        var linked = (await mirror.CommitsSinceAsync(project.OrganizationId, repo.Id, since, ct))
            .Select(c => (Commit: c, Ids: WorkItemMapping.WorkItemReferences(c.Message))).Where(x => x.Ids.Count > 0).ToList();
        if (linked.Count == 0) return 0;
        var shas = linked.Select(x => x.Commit.CommitId).ToList();
        var known = (await db.Commits.Where(c => c.RepositoryId == repo.Id && shas.Contains(c.CommitId)).Select(c => c.CommitId).ToListAsync(ct)).ToHashSet();
        var added = 0;
        foreach (var (commit, ids) in linked.Where(x => !known.Contains(x.Commit.CommitId)))
        {
            db.Commits.Add(new Commit
            {
                OrganizationId = project.OrganizationId, RepositoryId = repo.Id, CommitId = commit.CommitId, AuthorName = commit.AuthorName,
                AuthorEmail = commit.AuthorEmail, Message = commit.Message, CommittedAt = commit.AuthoredAt.ToUniversalTime(),
                WebUrl = $"{repo.WebUrl}/commit/{commit.CommitId}", WorkItemAzureIds = ids,
            });
            added++;
        }
        return added;
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

/// <summary>
/// One sync of a project at a time on this node: the interval, service hooks and "sync now" can fire
/// together, and overlapping reconciliations of the same project would race on inserts.
/// </summary>
internal static class ProjectSyncLocks
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    public static async Task<IDisposable> AcquireAsync(Guid projectId, CancellationToken ct)
    {
        var gate = Locks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Release(gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}

public static class SyncOrganizationHandler
{
    /// <summary>Returned project messages cascade through Wolverine, so projects sync in parallel in the background.</summary>
    public static async Task<(OrganizationSyncResult, IEnumerable<object>)> Handle(SyncOrganization message, ITenantContext tenant,
        OrganizationSynchronizer synchronizer, CancellationToken ct)
    {
        tenant.Use(message.OrganizationId);
        var (result, projects) = await synchronizer.SyncAsync(message.OrganizationId, message.Trigger, ct);
        return (result, projects);
    }
}

public static class SyncProjectHandler
{
    /// <summary>Returned messages cascade through Wolverine (queued runs go to the agent-runs queue).</summary>
    public static async Task<(SyncResult, IEnumerable<object>)> Handle(SyncProject message, ITenantContext tenant, ProjectSynchronizer synchronizer, CancellationToken ct)
    {
        tenant.Use(message.OrganizationId);
        using var _ = await ProjectSyncLocks.AcquireAsync(message.ProjectId, ct);
        var (result, runs) = await synchronizer.SyncAsync(message.ProjectId, ct);
        return (result, runs);
    }
}

public static class SyncWorkItemHandler
{
    public static async Task<IEnumerable<object>> Handle(SyncWorkItem message, ITenantContext tenant, ProjectSynchronizer synchronizer, CancellationToken ct)
    {
        tenant.Use(message.OrganizationId);
        using var _ = await ProjectSyncLocks.AcquireAsync(message.ProjectId, ct);
        return await synchronizer.SyncWorkItemAsync(message.ProjectId, message.WorkItemAzureId, ct);
    }
}
