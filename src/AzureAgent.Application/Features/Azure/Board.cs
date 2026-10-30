using System.Linq.Expressions;
using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Domain.Agents;
using AzureAgent.Domain.Azure;
using AzureAgent.Domain.Projects;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Features.Azure;

public sealed record WorkItemSummaryDto(int AzureId, string Type, string TypeColor, string Title, string State, string StateColor, string StateCategory,
    string? BoardColumn, string? AssignedToUniqueName, string? AssignedToDisplayName, int? Priority, double? StoryPoints, IReadOnlyList<string> Tags,
    IReadOnlyList<string> RepositoryNames, string? AreaPath, string? IterationPath, string WebUrl, DateTimeOffset ChangedAt, RunStatus? LastRunStatus,
    string? AgentName, decimal CostUsd);

internal static class WorkItemProjection
{
    public sealed record Row(WorkItem Item, RunStatus? LastRunStatus, string? AgentName, decimal CostUsd);

    public static IQueryable<Row> WithRuns(this IQueryable<WorkItem> items, IAppDbContext db) =>
        items.Select(w => new
            {
                w,
                LastRun = db.AgentRuns.Where(r => r.WorkItemId == w.Id).OrderByDescending(r => r.CreatedAt)
                    .Select(r => new { r.Status, Agent = db.Agents.Where(a => a.Id == r.AgentId).Select(a => a.Name).FirstOrDefault() }).FirstOrDefault(),
                Cost = db.AgentRuns.Where(r => r.WorkItemId == w.Id).Sum(r => (decimal?)r.CostUsd) ?? 0,
            })
            .Select(x => new Row(x.w, x.LastRun == null ? null : x.LastRun.Status, x.LastRun == null ? null : x.LastRun.Agent, x.Cost));

    public static WorkItemSummaryDto ToDto(this Row row, Project project)
    {
        var w = row.Item;
        var type = project.WorkItemTypes.FirstOrDefault(t => t.Name == w.Type);
        var state = type?.States.FirstOrDefault(s => s.Name == w.State);
        return new WorkItemSummaryDto(w.AzureId, w.Type, type?.Color ?? "", w.Title, w.State, state?.Color ?? "", state?.Category ?? "", w.BoardColumn,
            w.AssignedToUniqueName, w.AssignedToDisplayName, w.Priority, w.StoryPoints, w.Tags, w.RepositoryNames, w.AreaPath, w.IterationPath, w.WebUrl,
            w.ChangedAt, row.LastRunStatus, row.AgentName, row.CostUsd);
    }

    public static async Task<WorkItemSummaryDto> SummaryAsync(IAppDbContext db, Project project, int azureId, CancellationToken ct) =>
        (await db.WorkItems.AsNoTracking().Where(w => w.ProjectId == project.Id && w.AzureId == azureId).WithRuns(db).SingleAsync(ct)).ToDto(project);

    /// <summary>Work items in the team's area paths (Azure team field values), children included where the team says so.</summary>
    public static Expression<Func<WorkItem, bool>> TeamAreaFilter(IReadOnlyCollection<TeamArea> areas)
    {
        var item = Expression.Parameter(typeof(WorkItem), "w");
        var path = Expression.Property(item, nameof(WorkItem.AreaPath));
        Expression body = Expression.Constant(false);
        var startsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
        foreach (var area in areas)
        {
            Expression match = Expression.Equal(path, Expression.Constant(area.Path, typeof(string)));
            if (area.IncludeChildren)
                match = Expression.OrElse(match, Expression.AndAlso(Expression.NotEqual(path, Expression.Constant(null, typeof(string))),
                    Expression.Call(path, startsWith, Expression.Constant(area.Path + "\\"))));
            body = Expression.OrElse(body, match);
        }
        return Expression.Lambda<Func<WorkItem, bool>>(body, item);
    }

    public static async Task<string> MyAzureNameAsync(IAppDbContext db, ICurrentUser user, Guid projectId, CancellationToken ct) =>
        await db.ProjectMembers.Where(m => m.ProjectId == projectId && m.UserId == user.UserId).Select(m => m.AzureUniqueName).FirstOrDefaultAsync(ct)
        ?? user.Email ?? "";

    public static Task<Project> ProjectAsync(IAppDbContext db, Guid projectId, CancellationToken ct) =>
        db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
}

// Backlog / work item list ----------------------------------------------------------------------------

public sealed record ListWorkItemsQuery(Guid ProjectId, bool AssignedToMe = false, string? State = null, string? Search = null, string? Iteration = null,
    Guid? TeamId = null, bool Unassigned = false, string? Type = null, bool IncludeClosed = true);

public static class ListWorkItemsHandler
{
    public static async Task<IReadOnlyList<WorkItemSummaryDto>> Handle(ListWorkItemsQuery query, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.WorkItemsView, ct);
        var project = await WorkItemProjection.ProjectAsync(db, query.ProjectId, ct);
        var items = db.WorkItems.AsNoTracking().Where(w => w.ProjectId == query.ProjectId);
        if (query.AssignedToMe)
        {
            var me = await WorkItemProjection.MyAzureNameAsync(db, user, query.ProjectId, ct);
            items = items.Where(w => w.AssignedToUniqueName == me);
        }
        if (query.Unassigned) items = items.Where(w => w.AssignedToUniqueName == null);
        if (query.TeamId is { } teamId)
        {
            var team = await db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == teamId && t.ProjectId == query.ProjectId, ct) ?? throw new NotFoundException("Team");
            items = items.Where(WorkItemProjection.TeamAreaFilter(team.Areas));
        }
        if (!string.IsNullOrWhiteSpace(query.State)) items = items.Where(w => w.State == query.State);
        if (!string.IsNullOrWhiteSpace(query.Type)) items = items.Where(w => w.Type == query.Type);
        if (!string.IsNullOrWhiteSpace(query.Iteration)) items = items.Where(w => w.IterationPath == query.Iteration);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            items = int.TryParse(search.TrimStart('#'), out var id)
                ? items.Where(w => w.AzureId == id)
                : items.Where(w => w.Title.ToLower().Contains(search.ToLower()));
        }
        var rows = await items.OrderByDescending(w => w.ChangedAt).Take(1000).WithRuns(db).ToListAsync(ct);
        return rows.Where(r => query.IncludeClosed || !project.IsClosedState(r.Item.Type, r.Item.State)).Select(r => r.ToDto(project)).ToList();
    }
}

public sealed record MyWorkItemDto(Guid ProjectId, string ProjectName, WorkItemSummaryDto Item);

/// <summary>Open work assigned to the caller in every project (the "Assigned to me" page).</summary>
public sealed record ListMyWorkItemsQuery(bool IncludeClosed = false);

public static class ListMyWorkItemsHandler
{
    public static async Task<IReadOnlyList<MyWorkItemDto>> Handle(ListMyWorkItemsQuery query, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        var userId = access.RequireUser();
        access.RequireOrganization();
        var memberships = await db.ProjectMembers.AsNoTracking().Where(m => m.UserId == userId).Select(m => new { m.ProjectId, Name = m.AzureUniqueName ?? user.Email })
            .ToListAsync(ct);
        var projectIds = memberships.Select(m => m.ProjectId).ToList();
        var names = memberships.Select(m => m.Name).Append(user.Email).OfType<string>().Distinct().ToList();
        var projects = await db.Projects.AsNoTracking().Where(p => projectIds.Contains(p.Id) && !p.IsRemoved).ToDictionaryAsync(p => p.Id, ct);
        var rows = await db.WorkItems.AsNoTracking().Where(w => projectIds.Contains(w.ProjectId) && w.AssignedToUniqueName != null && names.Contains(w.AssignedToUniqueName))
            .OrderByDescending(w => w.ChangedAt).Take(500).WithRuns(db).ToListAsync(ct);
        return rows.Where(r => projects.ContainsKey(r.Item.ProjectId))
            .Where(r => query.IncludeClosed || !projects[r.Item.ProjectId].IsClosedState(r.Item.Type, r.Item.State))
            .Select(r => new MyWorkItemDto(r.Item.ProjectId, projects[r.Item.ProjectId].Name, r.ToDto(projects[r.Item.ProjectId]))).ToList();
    }
}

// Kanban board -----------------------------------------------------------------------------------------

public sealed record BoardColumnDto(string Name, string ColumnType, int ItemLimit, IReadOnlyDictionary<string, string> StateMappings,
    IReadOnlyList<WorkItemSummaryDto> Items);

public sealed record BoardDto(Guid TeamId, string TeamName, string Board, IReadOnlyList<string> Boards, IReadOnlyList<BoardColumnDto> Columns);

/// <summary>A team's Kanban board as configured in Azure Boards: its columns, WIP limits and the items in the team's area.</summary>
public sealed record GetBoardQuery(Guid ProjectId, Guid? TeamId, string? Board, bool AssignedToMe = false);

public static class GetBoardHandler
{
    private const int MaxDoneItems = 50;

    public static async Task<BoardDto> Handle(GetBoardQuery query, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.WorkItemsView, ct);
        var project = await WorkItemProjection.ProjectAsync(db, query.ProjectId, ct);
        var teams = await db.Teams.AsNoTracking().Where(t => t.ProjectId == query.ProjectId).OrderBy(t => t.Name).ToListAsync(ct);
        var team = (query.TeamId is { } id ? teams.FirstOrDefault(t => t.Id == id) : teams.FirstOrDefault(t => t.Name == $"{project.Name} Team") ?? teams.FirstOrDefault())
            ?? throw new NotFoundException("Team");
        var board = team.Boards.FirstOrDefault(b => b.Name == query.Board) ?? team.Boards.FirstOrDefault()
            ?? throw new BusinessRuleException($"Team {team.Name} has no board in Azure Boards");
        var types = board.Columns.SelectMany(c => c.StateMappings).Select(m => m.WorkItemType).Distinct().ToList();
        var items = db.WorkItems.AsNoTracking().Where(w => w.ProjectId == project.Id && types.Contains(w.Type)).Where(WorkItemProjection.TeamAreaFilter(team.Areas));
        if (query.AssignedToMe)
        {
            var me = await WorkItemProjection.MyAzureNameAsync(db, user, project.Id, ct);
            items = items.Where(w => w.AssignedToUniqueName == me);
        }
        var rows = await items.OrderBy(w => w.Priority ?? 99).ThenByDescending(w => w.ChangedAt).Take(2000).WithRuns(db).ToListAsync(ct);

        var columns = board.Columns.Select(column => (Column: column, Items: new List<WorkItemSummaryDto>())).ToList();
        foreach (var row in rows)
        {
            var w = row.Item;
            // The item's own board column wins when it names one of this board's columns; otherwise its state decides.
            var target = columns.FirstOrDefault(c => c.Column.Name == w.BoardColumn && c.Column.StateMappings.Any(m => m.WorkItemType == w.Type && m.State == w.State)).Column
                ?? columns.FirstOrDefault(c => c.Column.StateMappings.Any(m => m.WorkItemType == w.Type && m.State == w.State)).Column;
            if (target is null) continue;
            var list = columns.First(c => c.Column == target).Items;
            if (target.ColumnType == "outgoing" && list.Count >= MaxDoneItems) continue;
            list.Add(row.ToDto(project));
        }
        return new BoardDto(team.Id, team.Name, board.Name, team.Boards.Select(b => b.Name).ToList(), columns.Select(c => new BoardColumnDto(c.Column.Name,
            c.Column.ColumnType, c.Column.ItemLimit, c.Column.StateMappings.ToDictionary(m => m.WorkItemType, m => m.State), c.Items)).ToList());
    }
}

/// <summary>Drags a card to another column: writes the column's state for the item's type (and the column itself when columns share a state).</summary>
public sealed record MoveWorkItemCommand(Guid ProjectId, int AzureId, Guid TeamId, string Board, string Column);

public static class MoveWorkItemHandler
{
    public static async Task<(WorkItemSummaryDto, IEnumerable<object>)> Handle(MoveWorkItemCommand command, IAppDbContext db, AccessControl access, ICurrentUser user,
        AzureConnector connector, Dispatcher dispatcher, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.WorkItemsAssign, ct);
        var item = await db.WorkItems.FirstOrDefaultAsync(w => w.ProjectId == command.ProjectId && w.AzureId == command.AzureId, ct) ?? throw new NotFoundException("Work item");
        var team = await db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == command.TeamId && t.ProjectId == command.ProjectId, ct) ?? throw new NotFoundException("Team");
        var board = team.Boards.FirstOrDefault(b => b.Name == command.Board) ?? throw new NotFoundException("Board");
        var column = board.Columns.FirstOrDefault(c => c.Name == command.Column) ?? throw new NotFoundException("Board column");
        var state = column.StateMappings.FirstOrDefault(m => m.WorkItemType == item.Type)?.State
            ?? throw new BusinessRuleException($"{item.Type} items cannot be placed in {column.Name}");
        var fields = new Dictionary<string, object?> { ["System.State"] = state };
        if (board.ColumnField is { Length: > 0 } columnField && board.Columns.Count(c => c.StateMappings.Any(m => m.WorkItemType == item.Type && m.State == state)) > 1)
            fields[columnField] = column.Name;
        var (project, updated, asUser) = await connector.WriteAsUserAsync(command.ProjectId, access.RequireUser(),
            azure => azure.UpdateWorkItemFieldsAsync(command.AzureId, fields, ct), ct);
        WorkItemMapping.Apply(item, updated);
        item.BoardColumn = column.Name;
        db.Audit(user, project.OrganizationId, "work_item.moved", "work_item", command.AzureId, new { column = column.Name, state, asUser }, project.Id);
        await db.SaveChangesAsync(ct);
        var messages = await dispatcher.QueueAssignedWorkAsync(project, ct);
        await db.SaveChangesAsync(ct);
        return (await WorkItemProjection.SummaryAsync(db, project, command.AzureId, ct), messages);
    }
}

// Sprints ------------------------------------------------------------------------------------------------

public sealed record IterationDto(string Id, string Name, string Path, DateTimeOffset? StartDate, DateTimeOffset? FinishDate, string TimeFrame);

public sealed record SprintDto(Guid TeamId, string TeamName, IReadOnlyList<IterationDto> Iterations, IterationDto? Iteration, IReadOnlyList<WorkItemSummaryDto> Items);

/// <summary>A team's sprint (current by default) with its work items.</summary>
public sealed record GetSprintQuery(Guid ProjectId, Guid? TeamId, string? IterationId);

public static class GetSprintHandler
{
    public static async Task<SprintDto> Handle(GetSprintQuery query, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.WorkItemsView, ct);
        var project = await WorkItemProjection.ProjectAsync(db, query.ProjectId, ct);
        var teams = await db.Teams.AsNoTracking().Where(t => t.ProjectId == query.ProjectId).OrderBy(t => t.Name).ToListAsync(ct);
        var team = (query.TeamId is { } id ? teams.FirstOrDefault(t => t.Id == id) : teams.FirstOrDefault(t => t.Name == $"{project.Name} Team") ?? teams.FirstOrDefault())
            ?? throw new NotFoundException("Team");
        var iterations = team.Iterations.Select(i => new IterationDto(i.AzureId, i.Name, i.Path, i.StartDate, i.FinishDate, i.TimeFrame)).ToList();
        var iteration = iterations.FirstOrDefault(i => i.Id == query.IterationId || i.Path == query.IterationId) ?? iterations.FirstOrDefault(i => i.TimeFrame == "current") ?? iterations.LastOrDefault();
        IReadOnlyList<WorkItemSummaryDto> items = [];
        if (iteration is not null)
        {
            // Team iteration paths are "Project\Sprint 1"; work items store the same form.
            var path = iteration.Path;
            items = (await db.WorkItems.AsNoTracking().Where(w => w.ProjectId == project.Id && w.IterationPath == path)
                .OrderBy(w => w.Priority ?? 99).Take(1000).WithRuns(db).ToListAsync(ct)).Select(r => r.ToDto(project)).ToList();
        }
        return new SprintDto(team.Id, team.Name, iterations, iteration, items);
    }
}

// Work item detail ---------------------------------------------------------------------------------------

public sealed record RunSummaryDto(Guid Id, RunKind Kind, RunStatus Status, Guid AgentId, string AgentName, Guid ProjectId, int? WorkItemAzureId, string? Branch,
    IReadOnlyList<string> PullRequestUrls, string? Error, long InputTokens, long OutputTokens, decimal CostUsd, DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

public sealed record PullRequestDto(Guid Id, int AzureId, Guid ProjectId, Guid RepositoryId, string Repository, string Title, PullRequestStatus Status,
    string SourceBranch, string TargetBranch, string CreatedBy, bool CreatedByAgent, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, string WebUrl,
    IReadOnlyList<int> WorkItemAzureIds, ReviewVerdict? LastReviewVerdict);

public sealed record CommitDto(string CommitId, Guid RepositoryId, string Repository, string AuthorName, string Message, DateTimeOffset CommittedAt, string WebUrl);

public sealed record WorkItemDetailDto(WorkItemSummaryDto Summary, string Description, string AcceptanceCriteria, string? CreatedBy, DateTimeOffset? CreatedAt,
    IReadOnlyList<string> States, IReadOnlyList<RunSummaryDto> Runs, IReadOnlyList<PullRequestDto> PullRequests, IReadOnlyList<CommitDto> Commits);

internal static class Projections
{
    public static IQueryable<RunSummaryDto> RunSummaries(this IQueryable<AgentRun> runs, IAppDbContext db) =>
        runs.Join(db.Agents, r => r.AgentId, a => a.Id, (r, a) => new RunSummaryDto(r.Id, r.Kind, r.Status, a.Id, a.Name, r.ProjectId, r.WorkItemAzureId, r.Branch,
            r.PullRequestUrls, r.Error, r.InputTokens, r.OutputTokens, r.CostUsd, r.CreatedAt, r.StartedAt, r.FinishedAt));

    public static IQueryable<PullRequestDto> PullRequestSummaries(this IQueryable<PullRequest> prs, IAppDbContext db) =>
        prs.Join(db.Repositories, p => p.RepositoryId, r => r.Id, (p, r) => new { p, r })
            .Select(x => new PullRequestDto(x.p.Id, x.p.AzureId, x.r.ProjectId, x.r.Id, x.r.Name, x.p.Title, x.p.Status, x.p.SourceBranch, x.p.TargetBranch,
                x.p.CreatedBy, x.p.CreatedByAgent, x.p.OpenedAt, x.p.ClosedAt, x.p.WebUrl, x.p.WorkItemAzureIds,
                db.CodeReviews.Where(c => c.PullRequestId == x.p.Id).OrderByDescending(c => c.CreatedAt).Select(c => (ReviewVerdict?)c.Verdict).FirstOrDefault()));
}

public sealed record GetWorkItemQuery(Guid ProjectId, int AzureId);

public static class GetWorkItemHandler
{
    public static async Task<WorkItemDetailDto> Handle(GetWorkItemQuery query, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.WorkItemsView, ct);
        var project = await WorkItemProjection.ProjectAsync(db, query.ProjectId, ct);
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.ProjectId == query.ProjectId && w.AzureId == query.AzureId, ct)
            ?? throw new NotFoundException("Work item");
        var summary = await WorkItemProjection.SummaryAsync(db, project, query.AzureId, ct);
        var runs = await db.AgentRuns.AsNoTracking().Where(r => r.WorkItemId == item.Id).OrderByDescending(r => r.CreatedAt).RunSummaries(db).ToListAsync(ct);
        var repoIds = db.Repositories.Where(r => r.ProjectId == query.ProjectId).Select(r => r.Id);
        var prs = await db.PullRequests.AsNoTracking().Where(p => repoIds.Contains(p.RepositoryId) && p.WorkItemAzureIds.Contains(query.AzureId))
            .OrderByDescending(p => p.OpenedAt).PullRequestSummaries(db).ToListAsync(ct);
        var commits = await db.Commits.AsNoTracking().Where(c => repoIds.Contains(c.RepositoryId) && c.WorkItemAzureIds.Contains(query.AzureId))
            .Join(db.Repositories, c => c.RepositoryId, r => r.Id, (c, r) => new { c, Repository = r.Name })
            .OrderByDescending(x => x.c.CommittedAt).Take(100)
            .Select(x => new CommitDto(x.c.CommitId, x.c.RepositoryId, x.Repository, x.c.AuthorName, x.c.Message, x.c.CommittedAt, x.c.WebUrl)).ToListAsync(ct);
        var states = project.WorkItemTypes.FirstOrDefault(t => t.Name == item.Type)?.States.Select(s => s.Name).ToList() ?? [item.State];
        return new WorkItemDetailDto(summary, item.Description, item.AcceptanceCriteria, item.CreatedByDisplayName, item.AzureCreatedAt, states, runs, prs, commits);
    }
}

public sealed record CommentDto(int Id, string Text, string AuthorDisplayName, string AuthorUniqueName, DateTimeOffset CreatedDate);

/// <summary>The work item's discussion, read live from Azure Boards.</summary>
public sealed record ListWorkItemCommentsQuery(Guid ProjectId, int AzureId);

public static class ListWorkItemCommentsHandler
{
    public static async Task<IReadOnlyList<CommentDto>> Handle(ListWorkItemCommentsQuery query, AccessControl access, AzureConnector connector, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.WorkItemsView, ct);
        var (_, azure) = await connector.ConnectAsync(query.ProjectId, ct);
        return (await azure.ListWorkItemCommentsAsync(query.AzureId, ct)).Select(c => new CommentDto(c.Id, c.Text, c.AuthorDisplayName, c.AuthorUniqueName, c.CreatedDate)).ToList();
    }
}

public sealed record AddWorkItemCommentCommand(Guid ProjectId, int AzureId, string Text);

public sealed class AddWorkItemCommentCommandValidator : AbstractValidator<AddWorkItemCommentCommand>
{
    public AddWorkItemCommentCommandValidator() => RuleFor(x => x.Text).NotEmpty().MaximumLength(20_000);
}

public static class AddWorkItemCommentHandler
{
    public static async Task Handle(AddWorkItemCommentCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, AzureConnector connector, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.WorkItemsAssign, ct);
        var text = string.Join("", command.Text.Trim().Split('\n').Select(l => $"<div>{System.Net.WebUtility.HtmlEncode(l.TrimEnd('\r'))}</div>"));
        // Comments written with the service identity say who wrote them.
        var signed = $"<div><i>{System.Net.WebUtility.HtmlEncode(user.ActorName)} via Azure Agent:</i></div>{text}";
        var (project, _, asUser) = await connector.WriteAsUserAsync(command.ProjectId, access.RequireUser(), async (azure, personal) =>
        {
            await azure.AddWorkItemCommentAsync(command.AzureId, personal ? text : signed, ct);
            return true;
        }, ct);
        db.Audit(user, project.OrganizationId, "work_item.commented", "work_item", command.AzureId, new { asUser }, project.Id);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Edits a work item in Azure Boards (as the caller when connected); only the fields that are set change.</summary>
public sealed record UpdateWorkItemCommand(Guid ProjectId, int AzureId, string? Title, string? Description, string? AcceptanceCriteria, string? State,
    string? IterationPath, int? Priority, IReadOnlyList<string>? Tags);

public sealed class UpdateWorkItemCommandValidator : AbstractValidator<UpdateWorkItemCommand>
{
    public UpdateWorkItemCommandValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(255).When(x => x.Title is not null);
        RuleFor(x => x.Description).MaximumLength(32_000);
        RuleFor(x => x.AcceptanceCriteria).MaximumLength(32_000);
        RuleFor(x => x.Priority).InclusiveBetween(1, 4).When(x => x.Priority.HasValue);
        RuleForEach(x => x.Tags).NotEmpty().MaximumLength(100).Must(t => !t.Contains(';')).WithMessage("Tags cannot contain ';'");
    }
}

public static class UpdateWorkItemHandler
{
    public static async Task<(WorkItemSummaryDto, IEnumerable<object>)> Handle(UpdateWorkItemCommand command, IAppDbContext db, AccessControl access, ICurrentUser user,
        AzureConnector connector, Dispatcher dispatcher, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.WorkItemsAssign, ct);
        var item = await db.WorkItems.FirstOrDefaultAsync(w => w.ProjectId == command.ProjectId && w.AzureId == command.AzureId, ct) ?? throw new NotFoundException("Work item");
        var project = await WorkItemProjection.ProjectAsync(db, command.ProjectId, ct);
        var type = project.WorkItemTypes.FirstOrDefault(t => t.Name == item.Type);
        var fields = new Dictionary<string, object?>();
        if (command.Title is not null) fields["System.Title"] = command.Title.Trim();
        if (command.Description is not null) fields["System.Description"] = WorkItemText.Html(command.Description) ?? "";
        if (command.AcceptanceCriteria is not null && type?.Fields.Contains(WorkItemMapping.AcceptanceCriteriaField) != false)
            fields[WorkItemMapping.AcceptanceCriteriaField] = WorkItemText.Html(command.AcceptanceCriteria) ?? "";
        if (command.State is not null)
        {
            if (type is not null && type.States.All(s => s.Name != command.State)) throw new BusinessRuleException($"'{command.State}' is not a state of {item.Type}");
            fields["System.State"] = command.State;
        }
        if (command.IterationPath is not null) fields["System.IterationPath"] = command.IterationPath;
        if (command.Priority is { } priority && type?.Fields.Contains(WorkItemMapping.PriorityField) != false) fields[WorkItemMapping.PriorityField] = priority;
        if (command.Tags is not null) fields["System.Tags"] = string.Join("; ", command.Tags.Distinct(StringComparer.OrdinalIgnoreCase));
        if (fields.Count == 0) throw new BusinessRuleException("Nothing to change");
        var (_, updated, asUser) = await connector.WriteAsUserAsync(command.ProjectId, access.RequireUser(),
            azure => azure.UpdateWorkItemFieldsAsync(command.AzureId, fields, ct), ct);
        WorkItemMapping.Apply(item, updated);
        db.Audit(user, project.OrganizationId, "work_item.updated", "work_item", command.AzureId, new { fields = fields.Keys, asUser }, project.Id);
        await db.SaveChangesAsync(ct);
        var messages = await dispatcher.QueueAssignedWorkAsync(project, ct);
        await db.SaveChangesAsync(ct);
        return (await WorkItemProjection.SummaryAsync(db, project, command.AzureId, ct), messages);
    }
}

internal static class WorkItemText
{
    public static string? Html(string? text) => string.IsNullOrWhiteSpace(text) ? null
        : string.Join("", text.Trim().Split('\n').Select(l => $"<div>{System.Net.WebUtility.HtmlEncode(l.TrimEnd('\r'))}</div>"));
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
        var target = command.ToMe ? await WorkItemProjection.MyAzureNameAsync(db, user, command.ProjectId, ct) : command.UniqueName?.Trim().ToLowerInvariant();
        // The change is made with the caller's own Azure credential when connected, so Azure history shows them.
        var (project, updated, asUser) = await connector.WriteAsUserAsync(command.ProjectId, access.RequireUser(),
            azure => azure.UpdateWorkItemFieldsAsync(command.AzureId, new Dictionary<string, object?> { ["System.AssignedTo"] = target ?? "" }, ct), ct);
        WorkItemMapping.Apply(item, updated);
        db.Audit(user, project.OrganizationId, "work_item.assigned", "work_item", command.AzureId, new { to = target, asUser }, project.Id);
        await db.SaveChangesAsync(ct);
        var messages = await dispatcher.QueueAssignedWorkAsync(project, ct);
        await db.SaveChangesAsync(ct);
        return (await WorkItemProjection.SummaryAsync(db, project, command.AzureId, ct), messages);
    }
}

/// <summary>Creates a work item in Azure Boards (as the caller when connected) and mirrors it.</summary>
public sealed record CreateWorkItemCommand(Guid ProjectId, string Type, string Title, string? Description, string? AcceptanceCriteria,
    IReadOnlyList<string> RepositoryNames, IReadOnlyList<string>? Tags, bool AssignToMe, int? Priority, string? IterationPath = null, string? AgentSlug = null);

public sealed class CreateWorkItemCommandValidator : AbstractValidator<CreateWorkItemCommand>
{
    public CreateWorkItemCommandValidator()
    {
        RuleFor(x => x.Type).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Title).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Description).MaximumLength(32_000);
        RuleFor(x => x.AcceptanceCriteria).MaximumLength(32_000);
        RuleFor(x => x.RepositoryNames).NotNull();
        RuleForEach(x => x.RepositoryNames).NotEmpty().MaximumLength(200);
        RuleForEach(x => x.Tags).NotEmpty().MaximumLength(100).Must(t => !t.Contains(';')).WithMessage("Tags cannot contain ';'");
        RuleFor(x => x.Priority).InclusiveBetween(1, 4).When(x => x.Priority.HasValue);
        RuleFor(x => x.AgentSlug).Matches("^[a-z0-9-]+$").When(x => x.AgentSlug is not null);
    }
}

public static class CreateWorkItemHandler
{
    public static async Task<(WorkItemSummaryDto, IEnumerable<object>)> Handle(CreateWorkItemCommand command, IAppDbContext db, AccessControl access,
        ICurrentUser user, AzureConnector connector, Dispatcher dispatcher, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.WorkItemsCreate, ct);
        var repositories = await db.Repositories.AsNoTracking().Where(r => r.ProjectId == command.ProjectId && !r.IsRemoved).Select(r => r.Name).ToListAsync(ct);
        var unknown = command.RepositoryNames.Where(n => !repositories.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0) throw new BusinessRuleException($"Unknown repositories: {string.Join(", ", unknown)}");
        if (command.AgentSlug is { } slug && !await db.Agents.AnyAsync(a => a.Slug == slug, ct)) throw new BusinessRuleException($"Unknown agent: {slug}");

        var project = await WorkItemProjection.ProjectAsync(db, command.ProjectId, ct);
        var tags = command.RepositoryNames.Select(n => $"repo:{repositories.First(r => r.Equals(n, StringComparison.OrdinalIgnoreCase))}")
            .Concat(command.AgentSlug is { } agent ? [$"agent:{agent}"] : [])
            .Concat(command.Tags ?? []).Distinct(StringComparer.OrdinalIgnoreCase);
        var assignee = command.AssignToMe ? await WorkItemProjection.MyAzureNameAsync(db, user, command.ProjectId, ct) : null;
        var type = project.WorkItemTypes.FirstOrDefault(t => t.Name.Equals(command.Type, StringComparison.OrdinalIgnoreCase))
            ?? throw new BusinessRuleException($"Work item type '{command.Type}' does not exist in {project.AzureProjectName}");
        var fields = new Dictionary<string, object?>
        {
            ["System.Title"] = command.Title.Trim(),
            ["System.Description"] = WorkItemText.Html(command.Description),
            ["System.Tags"] = string.Join("; ", tags),
            ["System.AssignedTo"] = assignee,
            ["System.IterationPath"] = command.IterationPath,
        };
        if (WorkItemText.Html(command.AcceptanceCriteria) is { } criteria)
        {
            // The Basic process (Issue/Task) has no acceptance criteria field; keep the criteria in the description there.
            if (type.Fields.Contains(WorkItemMapping.AcceptanceCriteriaField)) fields[WorkItemMapping.AcceptanceCriteriaField] = criteria;
            else fields["System.Description"] = $"{fields["System.Description"]}<h3>Acceptance criteria</h3>{criteria}";
        }
        if (command.Priority is { } priority && type.Fields.Contains(WorkItemMapping.PriorityField)) fields[WorkItemMapping.PriorityField] = priority;
        var (_, (created, webUrl), asUser) = await connector.WriteAsUserAsync(command.ProjectId, access.RequireUser(), async azure =>
        {
            var result = await azure.CreateWorkItemAsync(type.Name, fields, ct);
            return (result, azure.WorkItemWebUrl(result.Id));
        }, ct);
        var item = new WorkItem
        {
            OrganizationId = project.OrganizationId, ProjectId = project.Id, AzureId = created.Id, Type = "", Title = "", State = "",
            WebUrl = webUrl,
        };
        WorkItemMapping.Apply(item, created);
        db.WorkItems.Add(item);
        db.Audit(user, project.OrganizationId, "work_item.created", "work_item", created.Id, new { command.Type, command.Title, asUser }, project.Id);
        await db.SaveChangesAsync(ct);
        var messages = await dispatcher.QueueAssignedWorkAsync(project, ct);
        await db.SaveChangesAsync(ct);
        return (await WorkItemProjection.SummaryAsync(db, project, created.Id, ct), messages);
    }
}

// Teams ------------------------------------------------------------------------------------------------

public sealed record TeamDto(Guid Id, string Name, IReadOnlyList<TeamMemberDto> Members, IReadOnlyList<string> AreaPaths, IReadOnlyList<string> Boards,
    IReadOnlyList<IterationDto> Iterations);

public sealed record TeamMemberDto(string UniqueName, string DisplayName);

public sealed record ListTeamsQuery(Guid ProjectId);

public static class ListTeamsHandler
{
    public static async Task<IReadOnlyList<TeamDto>> Handle(ListTeamsQuery query, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.ProjectView, ct);
        var teams = await db.Teams.AsNoTracking().Where(t => t.ProjectId == query.ProjectId).OrderBy(t => t.Name).ToListAsync(ct);
        return teams.Select(t => new TeamDto(t.Id, t.Name, t.Members.OrderBy(m => m.DisplayName).Select(m => new TeamMemberDto(m.UniqueName, m.DisplayName)).ToList(),
            t.Areas.Select(a => a.IncludeChildren ? $"{a.Path} (+)" : a.Path).ToList(), t.Boards.Select(b => b.Name).ToList(),
            t.Iterations.Select(i => new IterationDto(i.AzureId, i.Name, i.Path, i.StartDate, i.FinishDate, i.TimeFrame)).ToList())).ToList();
    }
}

public sealed record WorkItemTypeDto(string Name, string Color, IReadOnlyList<WorkItemStateInfo> States, bool HasAcceptanceCriteria);

public sealed record ListWorkItemTypesQuery(Guid ProjectId);

public static class ListWorkItemTypesHandler
{
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "Test Case", "Test Plan", "Test Suite", "Shared Steps", "Shared Parameter", "Code Review Request", "Code Review Response",
        "Feedback Request", "Feedback Response",
    };

    public static async Task<IReadOnlyList<WorkItemTypeDto>> Handle(ListWorkItemTypesQuery query, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.WorkItemsView, ct);
        var project = await WorkItemProjection.ProjectAsync(db, query.ProjectId, ct);
        return project.WorkItemTypes.Where(t => !t.IsDisabled && !Hidden.Contains(t.Name))
            .Select(t => new WorkItemTypeDto(t.Name, t.Color, t.States, t.Fields.Contains(WorkItemMapping.AcceptanceCriteriaField))).ToList();
    }
}
