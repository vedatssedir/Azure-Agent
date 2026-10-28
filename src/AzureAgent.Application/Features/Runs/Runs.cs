using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Domain.Agents;
using AzureAgent.Domain.Identity;
using AzureAgent.Domain.Projects;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Features.Runs;

internal static class RunAccess
{
    /// <summary>Projects whose runs the caller may see.</summary>
    public static async Task<List<Guid>> VisibleProjectsAsync(IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        var visible = new List<Guid>();
        foreach (var id in await db.Projects.AsNoTracking().Select(p => p.Id).ToListAsync(ct))
            if ((await access.PermissionsAsync(id, null, ct)).Contains(Permission.AgentsView)) visible.Add(id);
        return visible;
    }

    public static async Task<AgentRun> RequireAsync(IAppDbContext db, AccessControl access, Guid runId, string permission, CancellationToken ct, bool tracking = false)
    {
        var runs = tracking ? db.AgentRuns : db.AgentRuns.AsNoTracking();
        var run = await runs.FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw new NotFoundException("Run");
        await access.RequireAsync(run.ProjectId, permission, ct);
        return run;
    }
}

public sealed record ListRunsQuery(Guid? ProjectId, RunStatus? Status, Guid? AgentId, bool Mine = false, int Take = 200);

public static class ListRunsHandler
{
    public static async Task<IReadOnlyList<RunSummaryDto>> Handle(ListRunsQuery query, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        var userId = access.RequireUser();
        access.RequireOrganization();
        var projects = await RunAccess.VisibleProjectsAsync(db, access, ct);
        if (query.ProjectId is { } projectId) projects = projects.Where(p => p == projectId).ToList();
        var mine = db.Agents.Where(a => a.OwnerUserId == userId).Select(a => a.Id);
        return await db.AgentRuns.AsNoTracking()
            .Where(r => projects.Contains(r.ProjectId) && (query.Status == null || r.Status == query.Status) && (query.AgentId == null || r.AgentId == query.AgentId))
            .Where(r => !query.Mine || mine.Contains(r.AgentId))
            .OrderByDescending(r => r.CreatedAt).Take(Math.Clamp(query.Take, 1, 500))
            .RunSummaries(db).ToListAsync(ct);
    }
}

public sealed record RunStepDto(int Sequence, RunStepType Type, string Title, string? Detail, long InputTokens, long OutputTokens, decimal CostUsd, int DurationMs,
    DateTimeOffset CreatedAt);

public sealed record RunDetailDto(RunSummaryDto Run, string ProjectName, string? WorkItemTitle, string? Summary, string? Provider, string? Model, int ModelCalls,
    int ToolCalls, long CachedInputTokens, string? RequestedBy, string? ApprovedBy, IReadOnlyList<RunStepDto> Steps);

public sealed record GetRunQuery(Guid RunId);

public static class GetRunHandler
{
    public static async Task<RunDetailDto> Handle(GetRunQuery query, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        var run = await RunAccess.RequireAsync(db, access, query.RunId, Permission.AgentsView, ct);
        var summary = await db.AgentRuns.AsNoTracking().Where(r => r.Id == run.Id).RunSummaries(db).SingleAsync(ct);
        var project = await db.Projects.AsNoTracking().Where(p => p.Id == run.ProjectId).Select(p => p.Name).FirstAsync(ct);
        var title = run.WorkItemId is { } itemId ? await db.WorkItems.AsNoTracking().Where(w => w.Id == itemId).Select(w => w.Title).FirstOrDefaultAsync(ct) : null;
        var steps = await db.AgentRunSteps.AsNoTracking().Where(s => s.RunId == run.Id).OrderBy(s => s.Sequence)
            .Select(s => new RunStepDto(s.Sequence, s.Type, s.Title, s.Detail, s.InputTokens, s.OutputTokens, s.CostUsd, s.DurationMs, s.CreatedAt)).ToListAsync(ct);
        return new RunDetailDto(summary, project, title, run.Summary, run.Provider, run.Model, run.ModelCalls, run.ToolCalls, run.CachedInputTokens, run.RequestedBy,
            run.ApprovedBy, steps);
    }
}

public sealed record ApproveRunCommand(Guid RunId);

public static class ApproveRunHandler
{
    public static async Task<ExecuteAgentRun> Handle(ApproveRunCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        var run = await RunAccess.RequireAsync(db, access, command.RunId, Permission.AgentsRun, ct, tracking: true);
        run.Approve(user.ActorName);
        db.Audit(user, run.OrganizationId, "run.approved", "run", run.Id, null, run.ProjectId);
        await db.SaveChangesAsync(ct);
        return new ExecuteAgentRun(run.OrganizationId, run.Id);
    }
}

public sealed record CancelRunCommand(Guid RunId);

public static class CancelRunHandler
{
    public static async Task Handle(CancelRunCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, IClock clock, CancellationToken ct)
    {
        var run = await RunAccess.RequireAsync(db, access, command.RunId, Permission.AgentsRun, ct, tracking: true);
        run.Cancel(clock.UtcNow);
        db.Audit(user, run.OrganizationId, "run.cancelled", "run", run.Id, null, run.ProjectId);
        await db.SaveChangesAsync(ct);
    }
}

public sealed record RetryRunCommand(Guid RunId);

public static class RetryRunHandler
{
    public static async Task<(Guid, IEnumerable<object>)> Handle(RetryRunCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        var previous = await RunAccess.RequireAsync(db, access, command.RunId, Permission.AgentsRun, ct);
        if (previous.Status is not (RunStatus.Failed or RunStatus.Cancelled)) throw new ConflictException("Only failed or cancelled runs can be retried");
        if (await db.AgentRuns.AnyAsync(r => r.WorkItemId == previous.WorkItemId && r.PullRequestId == previous.PullRequestId && r.Kind == previous.Kind
                && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running || r.Status == RunStatus.PendingApproval), ct))
            throw new ConflictException("This item already has an active run");
        var run = AgentRun.Create(previous.OrganizationId, previous.ProjectId, previous.AgentId, previous.Kind, false, previous.WorkItemId,
            previous.WorkItemAzureId, previous.PullRequestId, user.ActorName);
        db.AgentRuns.Add(run);
        db.Audit(user, run.OrganizationId, "run.retried", "run", run.Id, new { from = previous.Id }, run.ProjectId);
        await db.SaveChangesAsync(ct);
        return (run.Id, [new ExecuteAgentRun(run.OrganizationId, run.Id)]);
    }
}

/// <summary>Start one of the caller's agents (or, for admins, any agent) on a work item (implement) or pull request (review).</summary>
public sealed record StartRunCommand(Guid ProjectId, Guid AgentId, int? WorkItemAzureId, Guid? PullRequestId);

public sealed class StartRunCommandValidator : AbstractValidator<StartRunCommand>
{
    public StartRunCommandValidator() =>
        RuleFor(x => x).Must(x => x.WorkItemAzureId.HasValue ^ x.PullRequestId.HasValue).WithMessage("Choose either a work item or a pull request");
}

public static class StartRunHandler
{
    public static async Task<(Guid, IEnumerable<object>)> Handle(StartRunCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.AgentsRun, ct);
        var userId = access.RequireUser();
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == command.ProjectId, ct);
        var settings = await db.Organizations.AsNoTracking().Where(o => o.Id == project.OrganizationId).Select(o => o.Settings).FirstAsync(ct);
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == command.AgentId && a.Enabled && (a.ProjectId == null || a.ProjectId == project.Id), ct)
            ?? throw new NotFoundException("Enabled agent");
        var isAdmin = user.IsSystemAdmin || await access.OrganizationRoleAsync(ct) is OrganizationRole.Admin or OrganizationRole.Owner;
        if (!isAdmin && agent.OwnerUserId != userId && agent.OwnerUserId is not null) throw new ForbiddenException("You can only start your own agents");
        AgentRun run;
        if (command.WorkItemAzureId is { } azureId)
        {
            if (agent.Kind != AgentKind.Developer) throw new BusinessRuleException("Only developer agents implement work items");
            var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.ProjectId == project.Id && w.AzureId == azureId, ct) ?? throw new NotFoundException("Work item");
            run = AgentRun.Create(project.OrganizationId, project.Id, agent.Id, RunKind.Implement, settings.RequireRunApproval, item.Id, item.AzureId, requestedBy: user.ActorName);
        }
        else
        {
            if (agent.Kind != AgentKind.Reviewer) throw new BusinessRuleException("Only reviewer agents review pull requests");
            var pr = await db.PullRequests.AsNoTracking().FirstOrDefaultAsync(p => p.Id == command.PullRequestId, ct) ?? throw new NotFoundException("Pull request");
            await access.RequireAsync(command.ProjectId, Permission.PullRequestsReview, ct, pr.RepositoryId);
            run = AgentRun.Create(project.OrganizationId, project.Id, agent.Id, RunKind.Review, false, pullRequestId: pr.Id, requestedBy: user.ActorName);
        }
        db.AgentRuns.Add(run);
        db.Audit(user, project.OrganizationId, "run.started", "run", run.Id, command, project.Id);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException("This item already has an active run");
        }
        return (run.Id, run.Status == RunStatus.Queued ? [new ExecuteAgentRun(run.OrganizationId, run.Id)] : []);
    }
}
