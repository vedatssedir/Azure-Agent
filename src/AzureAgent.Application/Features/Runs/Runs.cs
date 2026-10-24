using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Domain.Agents;
using AzureAgent.Domain.Projects;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Features.Runs;

public sealed record ListRunsQuery(Guid ProjectId, RunStatus? Status, Guid? AgentId, int Take = 200);

public static class ListRunsHandler
{
    public static async Task<IReadOnlyList<RunSummaryDto>> Handle(ListRunsQuery query, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.AgentsView, ct);
        return await db.AgentRuns.AsNoTracking()
            .Where(r => r.ProjectId == query.ProjectId && (query.Status == null || r.Status == query.Status) && (query.AgentId == null || r.AgentId == query.AgentId))
            .OrderByDescending(r => r.CreatedAt).Take(Math.Clamp(query.Take, 1, 500))
            .Join(db.Agents, r => r.AgentId, a => a.Id, (r, a) => new RunSummaryDto(r.Id, r.Kind, r.Status, a.Name, r.WorkItemAzureId, r.Branch,
                r.PullRequestUrls, r.Error, r.InputTokens, r.OutputTokens, r.CostUsd, r.CreatedAt, r.StartedAt, r.FinishedAt))
            .ToListAsync(ct);
    }
}

public sealed record RunStepDto(int Sequence, RunStepType Type, string Title, string? Detail, long InputTokens, long OutputTokens, decimal CostUsd, int DurationMs,
    DateTimeOffset CreatedAt);

public sealed record RunDetailDto(RunSummaryDto Run, string? Summary, string? Provider, string? Model, int ModelCalls, int ToolCalls, long CachedInputTokens,
    string? RequestedBy, string? ApprovedBy, IReadOnlyList<RunStepDto> Steps);

public sealed record GetRunQuery(Guid ProjectId, Guid RunId);

public static class GetRunHandler
{
    public static async Task<RunDetailDto> Handle(GetRunQuery query, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.AgentsView, ct);
        var run = await db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == query.RunId && r.ProjectId == query.ProjectId, ct) ?? throw new NotFoundException("Run");
        var agent = await db.Agents.AsNoTracking().Where(a => a.Id == run.AgentId).Select(a => a.Name).FirstAsync(ct);
        var steps = await db.AgentRunSteps.AsNoTracking().Where(s => s.RunId == run.Id).OrderBy(s => s.Sequence)
            .Select(s => new RunStepDto(s.Sequence, s.Type, s.Title, s.Detail, s.InputTokens, s.OutputTokens, s.CostUsd, s.DurationMs, s.CreatedAt)).ToListAsync(ct);
        return new RunDetailDto(
            new RunSummaryDto(run.Id, run.Kind, run.Status, agent, run.WorkItemAzureId, run.Branch, run.PullRequestUrls, run.Error, run.InputTokens,
                run.OutputTokens, run.CostUsd, run.CreatedAt, run.StartedAt, run.FinishedAt),
            run.Summary, run.Provider, run.Model, run.ModelCalls, run.ToolCalls, run.CachedInputTokens, run.RequestedBy, run.ApprovedBy, steps);
    }
}

public sealed record ApproveRunCommand(Guid ProjectId, Guid RunId);

public static class ApproveRunHandler
{
    public static async Task<ExecuteAgentRun> Handle(ApproveRunCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.AgentsRun, ct);
        var run = await db.AgentRuns.FirstOrDefaultAsync(r => r.Id == command.RunId && r.ProjectId == command.ProjectId, ct) ?? throw new NotFoundException("Run");
        try { run.Approve(user.ActorName); } catch (Domain.Common.DomainException e) { throw new ConflictException(e.Message); }
        db.Audit(user, run.OrganizationId, "run.approved", "run", run.Id, null, run.ProjectId);
        await db.SaveChangesAsync(ct);
        return new ExecuteAgentRun(run.OrganizationId, run.Id);
    }
}

public sealed record CancelRunCommand(Guid ProjectId, Guid RunId);

public static class CancelRunHandler
{
    public static async Task Handle(CancelRunCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, IClock clock, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.AgentsRun, ct);
        var run = await db.AgentRuns.FirstOrDefaultAsync(r => r.Id == command.RunId && r.ProjectId == command.ProjectId, ct) ?? throw new NotFoundException("Run");
        try { run.Cancel(clock.UtcNow); } catch (Domain.Common.DomainException e) { throw new ConflictException(e.Message); }
        db.Audit(user, run.OrganizationId, "run.cancelled", "run", run.Id, null, run.ProjectId);
        await db.SaveChangesAsync(ct);
    }
}

public sealed record RetryRunCommand(Guid ProjectId, Guid RunId);

public static class RetryRunHandler
{
    public static async Task<(Guid, IEnumerable<object>)> Handle(RetryRunCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.AgentsRun, ct);
        var previous = await db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == command.RunId && r.ProjectId == command.ProjectId, ct) ?? throw new NotFoundException("Run");
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

/// <summary>Manually start an agent on a work item (implement) or pull request (review).</summary>
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
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == command.ProjectId, ct);
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == command.AgentId && a.ProjectId == project.Id && a.Enabled, ct)
            ?? throw new NotFoundException("Enabled agent");
        AgentRun run;
        if (command.WorkItemAzureId is { } azureId)
        {
            if (agent.Kind != AgentKind.Developer) throw new BusinessRuleException("Only developer agents implement work items");
            var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.ProjectId == project.Id && w.AzureId == azureId, ct) ?? throw new NotFoundException("Work item");
            run = AgentRun.Create(project.OrganizationId, project.Id, agent.Id, RunKind.Implement, project.Settings.RequireRunApproval, item.Id, item.AzureId, requestedBy: user.ActorName);
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
