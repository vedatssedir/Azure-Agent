using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Domain.Agents;
using AzureAgent.Domain.Costs;
using AzureAgent.Domain.Projects;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Features.Costs;

public enum CostGrouping
{
    Agent = 0,
    WorkItem = 1,
    Day = 2,
    Model = 3,
    Kind = 4,
    Project = 5,
    Developer = 6,
}

public sealed record CostRow(string Key, string Label, int Runs, int SucceededRuns, long InputTokens, long OutputTokens, decimal CostUsd,
    decimal AverageCostPerRunUsd, double AverageDurationMinutes);

public sealed record CostReport(DateTimeOffset From, DateTimeOffset To, CostGrouping GroupBy, decimal TotalCostUsd, long TotalTokens, int TotalRuns,
    decimal CostPerSucceededRunUsd, IReadOnlyList<CostRow> Rows);

/// <summary>Cost-per-job analysis across the organization (or one project) grouped by agent, developer, project, work item, day, model or run kind.</summary>
public sealed record GetCostReportQuery(Guid? ProjectId, DateTimeOffset? From, DateTimeOffset? To, CostGrouping GroupBy);

public sealed class GetCostReportQueryValidator : AbstractValidator<GetCostReportQuery>
{
    public GetCostReportQueryValidator()
    {
        RuleFor(x => x.GroupBy).IsInEnum();
        RuleFor(x => x).Must(x => x.From is null || x.To is null || x.From < x.To).WithMessage("From must be before To");
        RuleFor(x => x).Must(x => x.From is null || x.To is null || x.To - x.From <= TimeSpan.FromDays(366)).WithMessage("Range is limited to one year");
    }
}

public static class GetCostReportHandler
{
    public static async Task<CostReport> Handle(GetCostReportQuery query, IAppDbContext db, AccessControl access, IClock clock, CancellationToken ct)
    {
        access.RequireOrganization();
        var projects = new List<Guid>();
        foreach (var id in await db.Projects.AsNoTracking().Where(p => query.ProjectId == null || p.Id == query.ProjectId).Select(p => p.Id).ToListAsync(ct))
            if ((await access.PermissionsAsync(id, null, ct)).Contains(Permission.CostsView)) projects.Add(id);
        if (projects.Count == 0) throw new ForbiddenException($"Missing permission '{Permission.CostsView}'");
        var to = query.To ?? clock.UtcNow;
        var from = query.From ?? to.AddDays(-30);
        var runs = await db.AgentRuns.AsNoTracking()
            .Where(r => projects.Contains(r.ProjectId) && r.CreatedAt >= from && r.CreatedAt < to)
            .Join(db.Agents, r => r.AgentId, a => a.Id, (r, a) => new
            {
                r.AgentId, AgentName = a.Name, a.OwnerUserId, r.ProjectId, r.WorkItemId, r.WorkItemAzureId, r.Kind, r.Status, r.InputTokens, r.OutputTokens,
                r.CostUsd, r.CreatedAt, r.StartedAt, r.FinishedAt, Model = r.Model ?? a.Model,
            })
            .ToListAsync(ct);
        var itemIds = runs.Where(r => r.WorkItemId != null).Select(r => r.WorkItemId!.Value).Distinct().ToList();
        var titles = await db.WorkItems.AsNoTracking().Where(w => itemIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Title, ct);
        var projectNames = await db.Projects.AsNoTracking().Where(p => projects.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var ownerIds = runs.Where(r => r.OwnerUserId != null).Select(r => r.OwnerUserId!.Value).Distinct().ToList();
        var owners = await db.Users.AsNoTracking().Where(u => ownerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var rows = runs.GroupBy(r => query.GroupBy switch
            {
                CostGrouping.Agent => (r.AgentId.ToString(), r.AgentName),
                CostGrouping.WorkItem => r.WorkItemId is { } id ? (id.ToString(), $"#{r.WorkItemAzureId} {titles.GetValueOrDefault(id)}") : ("review", "Pull request reviews"),
                CostGrouping.Day => (r.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd"), r.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd")),
                CostGrouping.Model => (r.Model, r.Model),
                CostGrouping.Project => (r.ProjectId.ToString(), projectNames.GetValueOrDefault(r.ProjectId) ?? "?"),
                CostGrouping.Developer => r.OwnerUserId is { } o ? (o.ToString(), owners.GetValueOrDefault(o) ?? "?") : ("shared", "Shared agents"),
                _ => (r.Kind.ToString(), r.Kind.ToString()),
            })
            .Select(g =>
            {
                var durations = g.Where(r => r.StartedAt.HasValue && r.FinishedAt.HasValue).Select(r => (r.FinishedAt!.Value - r.StartedAt!.Value).TotalMinutes).ToList();
                var cost = g.Sum(r => r.CostUsd);
                return new CostRow(g.Key.Item1, g.Key.Item2, g.Count(), g.Count(r => r.Status == RunStatus.Succeeded), g.Sum(r => r.InputTokens),
                    g.Sum(r => r.OutputTokens), cost, Math.Round(cost / g.Count(), 6), durations.Count > 0 ? Math.Round(durations.Average(), 2) : 0);
            })
            .OrderByDescending(r => query.GroupBy == CostGrouping.Day ? 0 : r.CostUsd).ThenByDescending(r => r.Key).ToList();
        var total = runs.Sum(r => r.CostUsd);
        var succeeded = runs.Count(r => r.Status == RunStatus.Succeeded);
        return new CostReport(from, to, query.GroupBy, total, runs.Sum(r => r.InputTokens + r.OutputTokens), runs.Count,
            succeeded > 0 ? Math.Round(total / succeeded, 6) : 0, rows);
    }
}

public sealed record BudgetLine(string Scope, string Name, decimal? LimitUsd, decimal SpentUsd, double? UsedPercent);

public sealed record GetBudgetStatusQuery;

public static class GetBudgetStatusHandler
{
    public static async Task<IReadOnlyList<BudgetLine>> Handle(GetBudgetStatusQuery _, AccessControl access, BudgetGuard budgets, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        return await budgets.StatusAsync(access.RequireOrganization(), null, ct);
    }
}

/// <summary>Monthly hard limits at organization, project and agent level. Runs stop when any is exhausted.</summary>
public sealed class BudgetGuard(IAppDbContext db, IClock clock)
{
    private DateTimeOffset MonthStart => new(clock.UtcNow.Year, clock.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero);

    private static BudgetLine Line(string scope, string name, decimal? limit, decimal spent) =>
        new(scope, name, limit, spent, limit is > 0 ? Math.Round((double)(spent / limit.Value) * 100, 1) : null);

    /// <summary>Organization, every project (or the given one) and every agent this month.</summary>
    public async Task<IReadOnlyList<BudgetLine>> StatusAsync(Guid organizationId, Guid? projectId, CancellationToken ct)
    {
        // Organizations are not tenant-filtered; the other sets are.
        var organization = await db.Organizations.AsNoTracking().FirstAsync(o => o.Id == organizationId, ct);
        var month = db.AgentRuns.AsNoTracking().Where(r => r.CreatedAt >= MonthStart);
        var orgSpent = await month.SumAsync(r => r.CostUsd, ct);
        var projectSpend = await month.GroupBy(r => r.ProjectId).Select(g => new { g.Key, Spent = g.Sum(r => r.CostUsd) }).ToDictionaryAsync(x => x.Key, x => x.Spent, ct);
        var agentSpend = await month.GroupBy(r => r.AgentId).Select(g => new { g.Key, Spent = g.Sum(r => r.CostUsd) }).ToDictionaryAsync(x => x.Key, x => x.Spent, ct);
        var projects = await db.Projects.AsNoTracking().Where(p => !p.IsRemoved && (projectId == null || p.Id == projectId)).OrderBy(p => p.Name).ToListAsync(ct);
        var agents = await db.Agents.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);
        return
        [
            Line("organization", organization.Name, organization.MonthlyBudgetUsd, orgSpent),
            .. projects.Select(p => Line("project", p.Name, p.MonthlyBudgetUsd, projectSpend.GetValueOrDefault(p.Id))),
            .. agents.Select(a => Line("agent", a.Name, a.MonthlyBudgetUsd, agentSpend.GetValueOrDefault(a.Id))),
        ];
    }

    /// <summary>Returns the exhausted budget, or null when the agent may spend on the project.</summary>
    public async Task<string?> ExhaustedAsync(Agent agent, Guid projectId, CancellationToken ct)
    {
        var organization = await db.Organizations.AsNoTracking().FirstAsync(o => o.Id == agent.OrganizationId, ct);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
        var month = db.AgentRuns.AsNoTracking().Where(r => r.CreatedAt >= MonthStart);
        if (organization.MonthlyBudgetUsd is { } orgLimit && await month.SumAsync(r => r.CostUsd, ct) is var orgSpent && orgSpent >= orgLimit)
            return $"organization budget for {organization.Name} is exhausted ({orgSpent:0.00}/{orgLimit:0.00} USD)";
        if (project.MonthlyBudgetUsd is { } projectLimit && await month.Where(r => r.ProjectId == projectId).SumAsync(r => r.CostUsd, ct) is var projectSpent && projectSpent >= projectLimit)
            return $"project budget for {project.Name} is exhausted ({projectSpent:0.00}/{projectLimit:0.00} USD)";
        if (agent.MonthlyBudgetUsd is { } agentLimit && await month.Where(r => r.AgentId == agent.Id).SumAsync(r => r.CostUsd, ct) is var agentSpent && agentSpent >= agentLimit)
            return $"agent budget for {agent.Name} is exhausted ({agentSpent:0.00}/{agentLimit:0.00} USD)";
        return null;
    }
}

/// <summary>Built-in model prices (per 1M tokens) used to put a cost on every model call.</summary>
public sealed class PriceList(IAppDbContext db)
{
    public async Task<ModelPrice?> FindAsync(Guid organizationId, ModelProvider provider, string model, CancellationToken ct)
    {
        var candidates = await db.ModelPrices.AsNoTracking().Where(p => p.Provider == provider && (p.OrganizationId == organizationId || p.OrganizationId == null)).ToListAsync(ct);
        return candidates.Where(p => model.StartsWith(p.Model, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.OrganizationId.HasValue).ThenByDescending(p => p.Model.Length).FirstOrDefault();
    }
}
