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
}

public sealed record CostRow(string Key, string Label, int Runs, int SucceededRuns, long InputTokens, long OutputTokens, decimal CostUsd,
    decimal AverageCostPerRunUsd, double AverageDurationMinutes);

public sealed record CostReport(DateTimeOffset From, DateTimeOffset To, CostGrouping GroupBy, decimal TotalCostUsd, long TotalTokens, int TotalRuns,
    decimal CostPerSucceededRunUsd, IReadOnlyList<CostRow> Rows);

/// <summary>Cost-per-job analysis: spend, tokens and run outcomes grouped by agent, work item, day, model or run kind.</summary>
public sealed record GetCostReportQuery(Guid ProjectId, DateTimeOffset? From, DateTimeOffset? To, CostGrouping GroupBy);

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
        await access.RequireAsync(query.ProjectId, Permission.CostsView, ct);
        var to = query.To ?? clock.UtcNow;
        var from = query.From ?? to.AddDays(-30);
        var runs = await db.AgentRuns.AsNoTracking()
            .Where(r => r.ProjectId == query.ProjectId && r.CreatedAt >= from && r.CreatedAt < to)
            .Join(db.Agents, r => r.AgentId, a => a.Id, (r, a) => new
            {
                r.AgentId, AgentName = a.Name, r.WorkItemAzureId, r.Kind, r.Status, r.InputTokens, r.OutputTokens, r.CostUsd, r.CreatedAt,
                r.StartedAt, r.FinishedAt, Model = r.Model ?? a.Model,
            })
            .ToListAsync(ct);
        var titles = await db.WorkItems.AsNoTracking().Where(w => w.ProjectId == query.ProjectId).ToDictionaryAsync(w => w.AzureId, w => w.Title, ct);

        var rows = runs.GroupBy(r => query.GroupBy switch
            {
                CostGrouping.Agent => (r.AgentId.ToString(), r.AgentName),
                CostGrouping.WorkItem => r.WorkItemAzureId is { } id ? (id.ToString(), $"#{id} {titles.GetValueOrDefault(id)}") : ("review", "Pull request reviews"),
                CostGrouping.Day => (r.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd"), r.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd")),
                CostGrouping.Model => (r.Model, r.Model),
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

public sealed record GetBudgetStatusQuery(Guid ProjectId);

public static class GetBudgetStatusHandler
{
    public static async Task<IReadOnlyList<BudgetLine>> Handle(GetBudgetStatusQuery query, IAppDbContext db, AccessControl access, BudgetGuard budgets, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.CostsView, ct);
        return await budgets.StatusAsync(query.ProjectId, ct);
    }
}

/// <summary>Monthly hard limits at organization, project and agent level. Runs stop when any is exhausted.</summary>
public sealed class BudgetGuard(IAppDbContext db, IClock clock)
{
    private DateTimeOffset MonthStart => new(clock.UtcNow.Year, clock.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task<IReadOnlyList<BudgetLine>> StatusAsync(Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct);
        var organization = await db.Organizations.AsNoTracking().FirstAsync(o => o.Id == project.OrganizationId, ct);
        var month = db.AgentRuns.AsNoTracking().Where(r => r.CreatedAt >= MonthStart);
        var orgSpent = await month.SumAsync(r => r.CostUsd, ct);
        var projectSpent = await month.Where(r => r.ProjectId == projectId).SumAsync(r => r.CostUsd, ct);
        var agentSpend = await month.Where(r => r.ProjectId == projectId).GroupBy(r => r.AgentId).Select(g => new { g.Key, Spent = g.Sum(r => r.CostUsd) })
            .ToDictionaryAsync(x => x.Key, x => x.Spent, ct);
        var agents = await db.Agents.AsNoTracking().Where(a => a.ProjectId == projectId).OrderBy(a => a.Name).ToListAsync(ct);

        static BudgetLine Line(string scope, string name, decimal? limit, decimal spent) =>
            new(scope, name, limit, spent, limit is > 0 ? Math.Round((double)(spent / limit.Value) * 100, 1) : null);

        return
        [
            Line("organization", organization.Name, organization.MonthlyBudgetUsd, orgSpent),
            Line("project", project.Name, project.MonthlyBudgetUsd, projectSpent),
            .. agents.Select(a => Line("agent", a.Name, a.MonthlyBudgetUsd, agentSpend.GetValueOrDefault(a.Id))),
        ];
    }

    /// <summary>Returns the exhausted budget, or null when the agent may spend.</summary>
    public async Task<string?> ExhaustedAsync(Agent agent, CancellationToken ct)
    {
        foreach (var line in await StatusAsync(agent.ProjectId, ct))
        {
            if (line.Scope == "agent" && line.Name != agent.Name) continue;
            if (line.LimitUsd is { } limit && line.SpentUsd >= limit) return $"{line.Scope} budget for {line.Name} is exhausted ({line.SpentUsd:0.00}/{limit:0.00} USD)";
        }
        return null;
    }
}

// Model prices ----------------------------------------------------------------------------------

public sealed record ModelPriceDto(Guid Id, bool IsDefault, ModelProvider Provider, string Model, decimal InputPerMillion, decimal OutputPerMillion, decimal CachedInputPerMillion);

public sealed record ListModelPricesQuery;

public static class ListModelPricesHandler
{
    public static async Task<IReadOnlyList<ModelPriceDto>> Handle(ListModelPricesQuery _, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        var organizationId = access.RequireOrganization();
        if (await access.OrganizationRoleAsync(ct) is null) throw new ForbiddenException();
        return await db.ModelPrices.AsNoTracking().Where(p => p.OrganizationId == null || p.OrganizationId == organizationId)
            .OrderBy(p => p.Provider).ThenBy(p => p.Model)
            .Select(p => new ModelPriceDto(p.Id, p.OrganizationId == null, p.Provider, p.Model, p.InputPerMillion, p.OutputPerMillion, p.CachedInputPerMillion))
            .ToListAsync(ct);
    }
}

public sealed record SaveModelPriceCommand(ModelProvider Provider, string Model, decimal InputPerMillion, decimal OutputPerMillion, decimal CachedInputPerMillion);

public sealed class SaveModelPriceCommandValidator : AbstractValidator<SaveModelPriceCommand>
{
    public SaveModelPriceCommandValidator()
    {
        RuleFor(x => x.Provider).IsInEnum();
        RuleFor(x => x.Model).NotEmpty().MaximumLength(100);
        RuleFor(x => x.InputPerMillion).InclusiveBetween(0, 10_000);
        RuleFor(x => x.OutputPerMillion).InclusiveBetween(0, 10_000);
        RuleFor(x => x.CachedInputPerMillion).InclusiveBetween(0, 10_000);
    }
}

public static class SaveModelPriceHandler
{
    public static async Task Handle(SaveModelPriceCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        var organizationId = access.RequireOrganization();
        var price = await db.ModelPrices.FirstOrDefaultAsync(p => p.OrganizationId == organizationId && p.Provider == command.Provider && p.Model == command.Model, ct);
        if (price is null)
        {
            price = new ModelPrice { OrganizationId = organizationId, Provider = command.Provider, Model = command.Model };
            db.ModelPrices.Add(price);
        }
        price.InputPerMillion = command.InputPerMillion;
        price.OutputPerMillion = command.OutputPerMillion;
        price.CachedInputPerMillion = command.CachedInputPerMillion;
        db.Audit(user, organizationId, "model_price.saved", "model_price", $"{command.Provider}/{command.Model}", command);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Price lookup: organization override, then instance default, matching the longest model prefix.</summary>
public sealed class PriceList(IAppDbContext db)
{
    public async Task<ModelPrice?> FindAsync(Guid organizationId, ModelProvider provider, string model, CancellationToken ct)
    {
        var candidates = await db.ModelPrices.AsNoTracking().Where(p => p.Provider == provider && (p.OrganizationId == organizationId || p.OrganizationId == null)).ToListAsync(ct);
        return candidates.Where(p => model.StartsWith(p.Model, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.OrganizationId.HasValue).ThenByDescending(p => p.Model.Length).FirstOrDefault();
    }
}
