using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Application.Features.Identity;
using AzureAgent.Domain.Agents;
using AzureAgent.Domain.Identity;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Features.Agents;

public sealed record AgentDto(Guid Id, string Name, string Slug, string? Title, AgentKind Kind, AgentRuntime Runtime, ModelProvider Provider, string Model,
    string Instructions, Guid? OwnerUserId, string? OwnerName, string? OwnerEmail, Guid? ProjectId, string? ProjectName, bool IsDefault, bool Enabled,
    int MaxConcurrentRuns, decimal? MonthlyBudgetUsd, AgentSettings Settings, IReadOnlyList<AgentSkill> Skills, int ActiveRuns, int RunsThisMonth,
    decimal SpentThisMonthUsd, DateTimeOffset? LastRunAt, bool CanEdit);

internal static class AgentRules
{
    /// <summary>Claude Code runs Anthropic models; Cursor brings its own models (the provider is informational).</summary>
    public static bool Compatible(AgentRuntime runtime, ModelProvider provider) => runtime != AgentRuntime.ClaudeCode || provider == ModelProvider.Anthropic;

    public const string RuntimeMessage = "Claude Code agents use the Anthropic provider";

    public static async Task<bool> IsAdminAsync(AccessControl access, ICurrentUser user, CancellationToken ct) =>
        user.IsSystemAdmin || await access.OrganizationRoleAsync(ct) is OrganizationRole.Admin or OrganizationRole.Owner;

    /// <summary>Owners edit their own agents; organization admins edit every agent (and own the shared ones).</summary>
    public static bool CanEdit(Agent agent, Guid userId, bool isAdmin) => isAdmin || agent.OwnerUserId == userId;
}

internal static class AgentQueries
{
    public static async Task<IReadOnlyList<AgentDto>> ListAsync(IAppDbContext db, IClock clock, Guid userId, bool isAdmin, Func<IQueryable<Agent>, IQueryable<Agent>> filter,
        CancellationToken ct)
    {
        var monthStart = new DateTimeOffset(clock.UtcNow.Year, clock.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var agents = await filter(db.Agents.AsNoTracking()).OrderByDescending(a => a.Kind == AgentKind.Developer).ThenBy(a => a.Name).ToListAsync(ct);
        var ids = agents.Select(a => a.Id).ToList();
        var stats = await db.AgentRuns.AsNoTracking().Where(r => ids.Contains(r.AgentId)).GroupBy(r => r.AgentId)
            .Select(g => new
            {
                AgentId = g.Key,
                Active = g.Count(r => r.Status == RunStatus.Queued || r.Status == RunStatus.Running || r.Status == RunStatus.PendingApproval),
                Month = g.Count(r => r.CreatedAt >= monthStart),
                Spent = g.Where(r => r.CreatedAt >= monthStart).Sum(r => r.CostUsd),
                Last = g.Max(r => (DateTimeOffset?)r.CreatedAt),
            }).ToDictionaryAsync(x => x.AgentId, ct);
        var ownerIds = agents.Where(a => a.OwnerUserId != null).Select(a => a.OwnerUserId!.Value).Distinct().ToList();
        var owners = await db.Users.AsNoTracking().Where(u => ownerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        var projectIds = agents.Where(a => a.ProjectId != null).Select(a => a.ProjectId!.Value).Distinct().ToList();
        var projects = await db.Projects.AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return agents.Select(a =>
        {
            var owner = a.OwnerUserId is { } o ? owners.GetValueOrDefault(o) : null;
            var stat = stats.GetValueOrDefault(a.Id);
            return new AgentDto(a.Id, a.Name, a.Slug, a.Title, a.Kind, a.Runtime, a.Provider, a.Model, a.Instructions, a.OwnerUserId, owner?.DisplayName, owner?.Email,
                a.ProjectId, a.ProjectId is { } p ? projects.GetValueOrDefault(p) : null, a.IsDefault, a.Enabled, a.MaxConcurrentRuns, a.MonthlyBudgetUsd, a.Settings,
                a.Skills, stat?.Active ?? 0, stat?.Month ?? 0, stat?.Spent ?? 0, stat?.Last, AgentRules.CanEdit(a, userId, isAdmin));
        }).ToList();
    }
}

/// <summary>Agents of the organization; <paramref name="Mine"/> = the caller's own.</summary>
public sealed record ListAgentsQuery(bool Mine = false);

public static class ListAgentsHandler
{
    public static async Task<IReadOnlyList<AgentDto>> Handle(ListAgentsQuery query, IAppDbContext db, AccessControl access, ICurrentUser user, IClock clock, CancellationToken ct)
    {
        var userId = access.RequireUser();
        if (await access.OrganizationRoleAsync(ct) is null && !user.IsSystemAdmin) throw new ForbiddenException();
        var isAdmin = await AgentRules.IsAdminAsync(access, user, ct);
        return await AgentQueries.ListAsync(db, clock, userId, isAdmin, q => query.Mine ? q.Where(a => a.OwnerUserId == userId) : q, ct);
    }
}

public sealed record SaveAgentCommand(Guid? AgentId, string Name, string? Title, AgentKind Kind, AgentRuntime Runtime, ModelProvider Provider, string Model,
    string? Instructions, IReadOnlyList<AgentSkill>? Skills, Guid? OwnerUserId, Guid? ProjectId, bool IsDefault, bool Enabled, int MaxConcurrentRuns,
    decimal? MonthlyBudgetUsd, AgentSettings? Settings);

public sealed class SaveAgentCommandValidator : AbstractValidator<SaveAgentCommand>
{
    public SaveAgentCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Title).MaximumLength(100);
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Runtime).IsInEnum();
        RuleFor(x => x.Provider).IsInEnum();
        RuleFor(x => x.Model).NotEmpty().MaximumLength(100);
        // CLI model names go straight to --model; keep them to a plain identifier.
        RuleFor(x => x.Model).Matches("^[A-Za-z0-9][A-Za-z0-9._:/@-]*$").When(x => x.Runtime != AgentRuntime.Framework)
            .WithMessage("Model must be a plain model name (letters, digits, . _ - : / @)");
        RuleFor(x => x).Must(x => AgentRules.Compatible(x.Runtime, x.Provider)).WithName("Runtime").WithMessage(AgentRules.RuntimeMessage);
        RuleFor(x => x.Instructions).MaximumLength(20_000);
        RuleFor(x => x.Skills).Must(s => s is null || s.Count <= 30).WithMessage("At most 30 skills per agent");
        RuleForEach(x => x.Skills).ChildRules(s =>
        {
            s.RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
            s.RuleFor(x => x.Content).NotEmpty().MaximumLength(20_000);
        });
        RuleFor(x => x.MaxConcurrentRuns).InclusiveBetween(1, 10);
        RuleFor(x => x.MonthlyBudgetUsd).GreaterThanOrEqualTo(0).When(x => x.MonthlyBudgetUsd.HasValue);
        RuleFor(x => x.IsDefault).Equal(false).When(x => x.Kind == AgentKind.Reviewer).WithMessage("Reviewer agents cannot be a developer's default");
        When(x => x.Settings is not null, () =>
        {
            RuleFor(x => x.Settings!.Temperature).InclusiveBetween(0, 2).When(x => x.Settings!.Temperature.HasValue);
            RuleFor(x => x.Settings!.MaxIterations).InclusiveBetween(1, 200);
            RuleFor(x => x.Settings!.MaxTokensPerRun).InclusiveBetween(1_000, 10_000_000);
            RuleFor(x => x.Settings!.MaxCostPerRunUsd).InclusiveBetween(0.01m, 1000m);
            RuleFor(x => x.Settings!.TimeoutMinutes).InclusiveBetween(1, 240);
        });
    }
}

public static class SaveAgentHandler
{
    public static async Task<AgentDto> Handle(SaveAgentCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, IClock clock, CancellationToken ct)
    {
        var userId = access.RequireUser();
        var organizationId = access.RequireOrganization();
        if (await access.OrganizationRoleAsync(ct) is null && !user.IsSystemAdmin) throw new ForbiddenException();
        var isAdmin = await AgentRules.IsAdminAsync(access, user, ct);
        // Developers create agents for themselves; only admins create shared agents or agents for others.
        var owner = command.OwnerUserId ?? (isAdmin && command.Kind == AgentKind.Reviewer ? null : userId);
        if (!isAdmin && owner != userId) throw new ForbiddenException("You can only manage your own agents");
        if (owner is { } ownerId && !await db.OrganizationMembers.AnyAsync(m => m.UserId == ownerId, ct)) throw new BusinessRuleException("The owner is not a member of the organization");
        if (command.ProjectId is { } projectId && !await db.Projects.AnyAsync(p => p.Id == projectId, ct)) throw new NotFoundException("Project");

        Agent agent;
        if (command.AgentId is { } id)
        {
            agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw new NotFoundException("Agent");
            if (!AgentRules.CanEdit(agent, userId, isAdmin)) throw new ForbiddenException("You can only manage your own agents");
        }
        else
        {
            var taken = (await db.Agents.Select(a => a.Slug).ToListAsync(ct)).ToHashSet();
            agent = new Agent { OrganizationId = organizationId, Name = command.Name, Slug = AgentSlugs.Unique(command.Name, taken), Model = command.Model };
            db.Agents.Add(agent);
        }
        agent.Name = command.Name.Trim();
        agent.Title = string.IsNullOrWhiteSpace(command.Title) ? null : command.Title.Trim();
        agent.Kind = command.Kind;
        agent.Runtime = command.Runtime;
        agent.Provider = command.Provider;
        agent.Model = command.Model.Trim();
        agent.Instructions = command.Instructions ?? "";
        agent.Skills = (command.Skills ?? []).Select(s => new AgentSkill { Name = s.Name.Trim(), Content = s.Content, Enabled = s.Enabled }).ToList();
        agent.OwnerUserId = owner;
        agent.ProjectId = command.ProjectId;
        agent.Enabled = command.Enabled;
        agent.MaxConcurrentRuns = command.MaxConcurrentRuns;
        agent.MonthlyBudgetUsd = command.MonthlyBudgetUsd;
        agent.Settings = command.Settings ?? agent.Settings;
        agent.IsDefault = command.IsDefault && owner is not null;
        if (agent.IsDefault)
            foreach (var other in await db.Agents.Where(a => a.OwnerUserId == owner && a.Id != agent.Id && a.IsDefault).ToListAsync(ct)) other.IsDefault = false;
        db.Audit(user, organizationId, command.AgentId is null ? "agent.created" : "agent.updated", "agent", agent.Id,
            new { agent.Name, agent.Runtime, agent.Model, agent.Enabled, agent.IsDefault, owner, skills = agent.Skills.Count });
        await db.SaveChangesAsync(ct);
        return (await AgentQueries.ListAsync(db, clock, userId, isAdmin, q => q.Where(a => a.Id == agent.Id), ct)).Single();
    }
}

public sealed record DeleteAgentCommand(Guid AgentId);

public static class DeleteAgentHandler
{
    public static async Task Handle(DeleteAgentCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        var userId = access.RequireUser();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == command.AgentId, ct) ?? throw new NotFoundException("Agent");
        if (!AgentRules.CanEdit(agent, userId, await AgentRules.IsAdminAsync(access, user, ct))) throw new ForbiddenException("You can only manage your own agents");
        if (await db.AgentRuns.AnyAsync(r => r.AgentId == agent.Id && r.Status == RunStatus.Running, ct))
            throw new ConflictException("Stop the agent's running jobs first (disable the agent)");
        db.Agents.Remove(agent);
        db.Audit(user, agent.OrganizationId, "agent.deleted", "agent", agent.Id, new { agent.Name });
        await db.SaveChangesAsync(ct);
    }
}

public sealed record ListAgentRuntimesQuery;

public static class ListAgentRuntimesHandler
{
    public static Task<IReadOnlyList<AgentRuntimeInfo>> Handle(ListAgentRuntimesQuery _, IAgentRuntimeCatalog catalog, AccessControl access, CancellationToken ct)
    {
        access.RequireUser();
        return catalog.ListAsync(ct);
    }
}
