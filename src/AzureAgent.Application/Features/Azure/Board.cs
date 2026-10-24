using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Domain.Agents;
using AzureAgent.Domain.Azure;
using AzureAgent.Domain.Projects;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Features.Azure;

public sealed record WorkItemSummaryDto(int AzureId, string Type, string Title, string State, string? AssignedToUniqueName, string? AssignedToDisplayName,
    int? Priority, double? StoryPoints, IReadOnlyList<string> RepositoryNames, string? IterationPath, string WebUrl, DateTimeOffset ChangedAt,
    RunStatus? LastRunStatus, string? AgentName, decimal CostUsd);

public sealed record ListWorkItemsQuery(Guid ProjectId, bool AssignedToMe = false, string? State = null, string? Search = null, string? Iteration = null);

public static class ListWorkItemsHandler
{
    public static async Task<IReadOnlyList<WorkItemSummaryDto>> Handle(ListWorkItemsQuery query, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.WorkItemsView, ct);
        var items = db.WorkItems.AsNoTracking().Where(w => w.ProjectId == query.ProjectId);
        if (query.AssignedToMe)
        {
            var me = await db.ProjectMembers.Where(m => m.ProjectId == query.ProjectId && m.UserId == user.UserId).Select(m => m.AzureUniqueName).FirstOrDefaultAsync(ct)
                ?? user.Email;
            items = items.Where(w => w.AssignedToUniqueName == me);
        }
        if (!string.IsNullOrWhiteSpace(query.State)) items = items.Where(w => w.State == query.State);
        if (!string.IsNullOrWhiteSpace(query.Iteration)) items = items.Where(w => w.IterationPath == query.Iteration);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            items = int.TryParse(search.TrimStart('#'), out var id)
                ? items.Where(w => w.AzureId == id)
                : items.Where(w => w.Title.ToLower().Contains(search.ToLower()));
        }
        return await items.OrderByDescending(w => w.ChangedAt).Take(500)
            .Select(w => new
            {
                w,
                LastRun = db.AgentRuns.Where(r => r.WorkItemId == w.Id).OrderByDescending(r => r.CreatedAt)
                    .Select(r => new { r.Status, Agent = db.Agents.Where(a => a.Id == r.AgentId).Select(a => a.Name).FirstOrDefault() }).FirstOrDefault(),
                Cost = db.AgentRuns.Where(r => r.WorkItemId == w.Id).Sum(r => (decimal?)r.CostUsd) ?? 0,
            })
            .Select(x => new WorkItemSummaryDto(x.w.AzureId, x.w.Type, x.w.Title, x.w.State, x.w.AssignedToUniqueName, x.w.AssignedToDisplayName,
                x.w.Priority, x.w.StoryPoints, x.w.RepositoryNames, x.w.IterationPath, x.w.WebUrl, x.w.ChangedAt,
                x.LastRun == null ? null : x.LastRun.Status, x.LastRun == null ? null : x.LastRun.Agent, x.Cost))
            .ToListAsync(ct);
    }
}

public sealed record RunSummaryDto(Guid Id, RunKind Kind, RunStatus Status, string AgentName, int? WorkItemAzureId, string? Branch,
    IReadOnlyList<string> PullRequestUrls, string? Error, long InputTokens, long OutputTokens, decimal CostUsd, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

public sealed record PullRequestDto(Guid Id, int AzureId, string Repository, string Title, PullRequestStatus Status, string SourceBranch, string TargetBranch,
    string CreatedBy, bool CreatedByAgent, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, string WebUrl, IReadOnlyList<int> WorkItemAzureIds);

public sealed record CommitDto(string CommitId, string Repository, string AuthorName, string Message, DateTimeOffset CommittedAt, string WebUrl);

public sealed record WorkItemDetailDto(WorkItemSummaryDto Summary, string Description, string AcceptanceCriteria, string? AreaPath, IReadOnlyList<string> Tags,
    IReadOnlyList<RunSummaryDto> Runs, IReadOnlyList<PullRequestDto> PullRequests, IReadOnlyList<CommitDto> Commits);

public sealed record GetWorkItemQuery(Guid ProjectId, int AzureId);

public static class GetWorkItemHandler
{
    public static async Task<WorkItemDetailDto> Handle(GetWorkItemQuery query, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.WorkItemsView, ct);
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.ProjectId == query.ProjectId && w.AzureId == query.AzureId, ct)
            ?? throw new NotFoundException("Work item");
        var summary = (await ListWorkItemsHandler.Handle(new ListWorkItemsQuery(query.ProjectId, Search: $"#{query.AzureId}"), db, access, user, ct)).Single();
        var runs = await db.AgentRuns.AsNoTracking().Where(r => r.WorkItemId == item.Id).OrderByDescending(r => r.CreatedAt)
            .Join(db.Agents, r => r.AgentId, a => a.Id, (r, a) => new RunSummaryDto(r.Id, r.Kind, r.Status, a.Name, r.WorkItemAzureId, r.Branch,
                r.PullRequestUrls, r.Error, r.InputTokens, r.OutputTokens, r.CostUsd, r.CreatedAt, r.StartedAt, r.FinishedAt))
            .ToListAsync(ct);
        var repoIds = db.Repositories.Where(r => r.ProjectId == query.ProjectId);
        var prs = await db.PullRequests.AsNoTracking().Where(p => p.WorkItemAzureIds.Contains(query.AzureId))
            .Join(repoIds, p => p.RepositoryId, r => r.Id, (p, r) => new { p, Repository = r.Name })
            .OrderByDescending(x => x.p.OpenedAt)
            .Select(x => new PullRequestDto(x.p.Id, x.p.AzureId, x.Repository, x.p.Title, x.p.Status, x.p.SourceBranch, x.p.TargetBranch, x.p.CreatedBy, x.p.CreatedByAgent, x.p.OpenedAt, x.p.ClosedAt, x.p.WebUrl, x.p.WorkItemAzureIds)).ToListAsync(ct);
        var commits = await db.Commits.AsNoTracking().Where(c => c.WorkItemAzureIds.Contains(query.AzureId))
            .Join(repoIds, c => c.RepositoryId, r => r.Id, (c, r) => new { c, Repository = r.Name })
            .OrderByDescending(x => x.c.CommittedAt).Take(100)
            .Select(x => new CommitDto(x.c.CommitId, x.Repository, x.c.AuthorName, x.c.Message, x.c.CommittedAt, x.c.WebUrl)).ToListAsync(ct);
        return new WorkItemDetailDto(summary, item.Description, item.AcceptanceCriteria, item.AreaPath, item.Tags, runs, prs, commits);
    }
}

/// <summary>Assigns a work item in Azure (the source of truth), mirrors the result and dispatches.</summary>
public sealed record AssignWorkItemCommand(Guid ProjectId, int AzureId, bool ToMe, string? UniqueName);

public sealed class AssignWorkItemCommandValidator : AbstractValidator<AssignWorkItemCommand>
{
    public AssignWorkItemCommandValidator()
    {
        RuleFor(x => x.UniqueName).EmailAddress().When(x => !x.ToMe && !string.IsNullOrEmpty(x.UniqueName));
    }
}

public static class AssignWorkItemHandler
{
    public static async Task<(WorkItemSummaryDto, IEnumerable<object>)> Handle(AssignWorkItemCommand command, IAppDbContext db, AccessControl access,
        ICurrentUser user, AzureConnector connector, Dispatcher dispatcher, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.WorkItemsAssign, ct);
        var item = await db.WorkItems.FirstOrDefaultAsync(w => w.ProjectId == command.ProjectId && w.AzureId == command.AzureId, ct)
            ?? throw new NotFoundException("Work item");
        string? target = command.UniqueName?.Trim().ToLowerInvariant();
        if (command.ToMe)
        {
            target = await db.ProjectMembers.Where(m => m.ProjectId == command.ProjectId && m.UserId == user.UserId).Select(m => m.AzureUniqueName).FirstOrDefaultAsync(ct)
                ?? user.Email;
        }
        var (project, azure) = await connector.ConnectAsync(command.ProjectId, ct);
        var updated = await azure.UpdateWorkItemFieldsAsync(command.AzureId, new Dictionary<string, object?> { ["System.AssignedTo"] = target ?? "" }, ct);
        WorkItemMapping.Apply(item, updated);
        db.Audit(user, project.OrganizationId, "work_item.assigned", "work_item", command.AzureId, new { to = target }, project.Id);
        await db.SaveChangesAsync(ct);
        var messages = await dispatcher.QueueAssignedWorkAsync(project, ct);
        await db.SaveChangesAsync(ct);
        var summary = (await ListWorkItemsHandler.Handle(new ListWorkItemsQuery(command.ProjectId, Search: $"#{command.AzureId}"), db, access, user, ct)).Single();
        return (summary, messages);
    }
}

public sealed record ListPullRequestsQuery(Guid ProjectId, PullRequestStatus? Status, Guid? RepositoryId);

public static class ListPullRequestsHandler
{
    public static async Task<IReadOnlyList<PullRequestDto>> Handle(ListPullRequestsQuery query, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.ProjectView, ct, query.RepositoryId);
        var prs = db.PullRequests.AsNoTracking().Where(p => query.Status == null || p.Status == query.Status)
            .Where(p => query.RepositoryId == null || p.RepositoryId == query.RepositoryId);
        return await prs.Join(db.Repositories.Where(r => r.ProjectId == query.ProjectId), p => p.RepositoryId, r => r.Id,
                (p, r) => new { p, Repository = r.Name })
            .OrderByDescending(x => x.p.OpenedAt).Take(300)
            .Select(x => new PullRequestDto(x.p.Id, x.p.AzureId, x.Repository, x.p.Title, x.p.Status, x.p.SourceBranch, x.p.TargetBranch, x.p.CreatedBy, x.p.CreatedByAgent, x.p.OpenedAt, x.p.ClosedAt, x.p.WebUrl, x.p.WorkItemAzureIds)).ToListAsync(ct);
    }
}

public sealed record ListCommitsQuery(Guid ProjectId, Guid RepositoryId);

public static class ListCommitsHandler
{
    public static async Task<IReadOnlyList<CommitDto>> Handle(ListCommitsQuery query, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.RepositoryView, ct, query.RepositoryId);
        return await db.Commits.AsNoTracking().Where(c => c.RepositoryId == query.RepositoryId)
            .Join(db.Repositories.Where(r => r.ProjectId == query.ProjectId), c => c.RepositoryId, r => r.Id,
                (c, r) => new { c, Repository = r.Name })
            .OrderByDescending(x => x.c.CommittedAt).Take(300)
            .Select(x => new CommitDto(x.c.CommitId, x.Repository, x.c.AuthorName, x.c.Message, x.c.CommittedAt, x.c.WebUrl)).ToListAsync(ct);
    }
}

public sealed record TeamDto(Guid Id, string Name, IReadOnlyList<TeamMemberDto> Members);

public sealed record TeamMemberDto(string UniqueName, string DisplayName, Guid? AgentId, string? AgentName);

public sealed record ListTeamsQuery(Guid ProjectId);

public static class ListTeamsHandler
{
    public static async Task<IReadOnlyList<TeamDto>> Handle(ListTeamsQuery query, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.ProjectView, ct);
        var teams = await db.Teams.AsNoTracking().Where(t => t.ProjectId == query.ProjectId).OrderBy(t => t.Name).ToListAsync(ct);
        var agents = await db.Agents.AsNoTracking().Where(a => a.ProjectId == query.ProjectId && a.PairedUniqueName != null)
            .ToDictionaryAsync(a => a.PairedUniqueName!, ct);
        return teams.Select(t => new TeamDto(t.Id, t.Name, t.Members.OrderBy(m => m.DisplayName).Select(m =>
            new TeamMemberDto(m.UniqueName, m.DisplayName, agents.GetValueOrDefault(m.UniqueName)?.Id, agents.GetValueOrDefault(m.UniqueName)?.Name)).ToList())).ToList();
    }
}
