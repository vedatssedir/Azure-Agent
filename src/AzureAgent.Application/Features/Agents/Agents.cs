using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Domain.Agents;
using AzureAgent.Domain.Projects;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Features.Agents;

public sealed record AgentDto(Guid Id, string Name, AgentKind Kind, ModelProvider Provider, string Model, string Instructions, string? PairedUniqueName,
    Guid? TeamId, bool Enabled, int MaxConcurrentRuns, decimal? MonthlyBudgetUsd, AgentSettings Settings, IReadOnlyList<string> Skills,
    IReadOnlyList<string> McpServers, int ActiveRuns, decimal SpentThisMonthUsd, AgentRuntime Runtime);

public sealed record SaveAgentCommand(Guid ProjectId, Guid? AgentId, string Name, AgentKind Kind, ModelProvider Provider, string Model, string? Instructions,
    string? PairedUniqueName, Guid? TeamId, bool Enabled, int MaxConcurrentRuns, decimal? MonthlyBudgetUsd, AgentSettings? Settings,
    AgentRuntime Runtime = AgentRuntime.Framework);

internal static class RuntimeRules
{
    /// <summary>Claude Code runs Anthropic models; Cursor brings its own models (the provider is informational).</summary>
    public static bool Compatible(AgentRuntime runtime, ModelProvider provider) => runtime != AgentRuntime.ClaudeCode || provider == ModelProvider.Anthropic;

    public const string Message = "Claude Code agents use the Anthropic provider";
}

public sealed class SaveAgentCommandValidator : AbstractValidator<SaveAgentCommand>
{
    public SaveAgentCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Provider).IsInEnum();
        RuleFor(x => x.Model).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Instructions).MaximumLength(20_000);
        RuleFor(x => x.PairedUniqueName).EmailAddress().When(x => !string.IsNullOrEmpty(x.PairedUniqueName));
        RuleFor(x => x.PairedUniqueName).Empty().When(x => x.Kind == AgentKind.Reviewer).WithMessage("Reviewer agents are not paired with a developer");
        RuleFor(x => x.MaxConcurrentRuns).InclusiveBetween(1, 10);
        RuleFor(x => x.MonthlyBudgetUsd).GreaterThanOrEqualTo(0).When(x => x.MonthlyBudgetUsd.HasValue);
        When(x => x.Settings is not null, () =>
        {
            RuleFor(x => x.Settings!.Temperature).InclusiveBetween(0, 2).When(x => x.Settings!.Temperature.HasValue);
            RuleFor(x => x.Settings!.MaxIterations).InclusiveBetween(1, 200);
            RuleFor(x => x.Settings!.MaxTokensPerRun).InclusiveBetween(1_000, 10_000_000);
            RuleFor(x => x.Settings!.MaxCostPerRunUsd).InclusiveBetween(0.01m, 1000m);
            RuleFor(x => x.Settings!.TimeoutMinutes).InclusiveBetween(1, 240);
            RuleFor(x => x.Settings!.Endpoint).Must(e => Uri.TryCreate(e, UriKind.Absolute, out var u) && u.Scheme == "https")
                .When(x => !string.IsNullOrEmpty(x.Settings!.Endpoint)).WithMessage("Endpoint must be an https URL");
        });
        RuleFor(x => x.Settings).Must(s => !string.IsNullOrEmpty(s?.Endpoint)).When(x => x.Provider == ModelProvider.AzureOpenAI && x.Runtime == AgentRuntime.Framework)
            .WithMessage("Azure OpenAI agents need an endpoint (Settings.Endpoint)");
        RuleFor(x => x.Runtime).IsInEnum();
        RuleFor(x => x).Must(x => RuntimeRules.Compatible(x.Runtime, x.Provider)).WithName("Runtime").WithMessage(RuntimeRules.Message);
        // CLI model names go straight to --model; keep them to a plain identifier.
        RuleFor(x => x.Model).Matches("^[A-Za-z0-9][A-Za-z0-9._:/@-]*$").When(x => x.Runtime != AgentRuntime.Framework)
            .WithMessage("Model must be a plain model name (letters, digits, . _ - : / @)");
    }
}

internal static class AgentQueries
{
    public static async Task<IReadOnlyList<AgentDto>> ListAsync(IAppDbContext db, IClock clock, Guid projectId, Guid? agentId, CancellationToken ct)
    {
        var monthStart = new DateTimeOffset(clock.UtcNow.Year, clock.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var skills = await db.Skills.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Key, ct);
        var mcp = await db.McpServers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var agents = await db.Agents.AsNoTracking().Include(a => a.Skills).Include(a => a.McpServers)
            .Where(a => a.ProjectId == projectId && (agentId == null || a.Id == agentId)).OrderBy(a => a.Name).ToListAsync(ct);
        var ids = agents.Select(a => a.Id).ToList();
        var stats = await db.AgentRuns.AsNoTracking().Where(r => ids.Contains(r.AgentId))
            .GroupBy(r => r.AgentId)
            .Select(g => new
            {
                AgentId = g.Key,
                Active = g.Count(r => r.Status == RunStatus.Queued || r.Status == RunStatus.Running),
                Spent = g.Where(r => r.CreatedAt >= monthStart).Sum(r => r.CostUsd),
            }).ToDictionaryAsync(x => x.AgentId, ct);
        return agents.Select(a => new AgentDto(a.Id, a.Name, a.Kind, a.Provider, a.Model, a.Instructions, a.PairedUniqueName, a.TeamId, a.Enabled,
            a.MaxConcurrentRuns, a.MonthlyBudgetUsd, a.Settings,
            a.Skills.Select(s => skills.GetValueOrDefault(s.SkillId)).OfType<string>().Order().ToList(),
            a.McpServers.Select(s => mcp.GetValueOrDefault(s.McpServerId)).OfType<string>().Order().ToList(),
            stats.GetValueOrDefault(a.Id)?.Active ?? 0, stats.GetValueOrDefault(a.Id)?.Spent ?? 0, a.Runtime)).ToList();
    }
}

public sealed record ListAgentsQuery(Guid ProjectId);

public static class ListAgentsHandler
{
    public static async Task<IReadOnlyList<AgentDto>> Handle(ListAgentsQuery query, IAppDbContext db, AccessControl access, IClock clock, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.AgentsView, ct);
        return await AgentQueries.ListAsync(db, clock, query.ProjectId, null, ct);
    }
}

public static class SaveAgentHandler
{
    public static async Task<(AgentDto, IEnumerable<object>)> Handle(SaveAgentCommand command, IAppDbContext db, AccessControl access, ICurrentUser user,
        IClock clock, Dispatcher dispatcher, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.AgentsManage, ct);
        var project = await db.Projects.FirstAsync(p => p.Id == command.ProjectId, ct);
        var paired = string.IsNullOrWhiteSpace(command.PairedUniqueName) ? null : command.PairedUniqueName.Trim().ToLowerInvariant();
        if (paired is not null && await db.Agents.AnyAsync(a => a.ProjectId == project.Id && a.PairedUniqueName == paired && a.Id != command.AgentId, ct))
            throw new ConflictException($"Another agent is already paired with {paired}");
        Agent agent;
        if (command.AgentId is { } id)
        {
            agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id && a.ProjectId == project.Id, ct) ?? throw new NotFoundException("Agent");
        }
        else
        {
            agent = new Agent { OrganizationId = project.OrganizationId, ProjectId = project.Id, Name = command.Name, Model = command.Model };
            db.Agents.Add(agent);
        }
        agent.Name = command.Name.Trim();
        agent.Kind = command.Kind;
        agent.Provider = command.Provider;
        agent.Runtime = command.Runtime;
        agent.Model = command.Model.Trim();
        agent.Instructions = command.Instructions ?? "";
        agent.PairedUniqueName = paired;
        agent.TeamId = command.TeamId;
        agent.Enabled = command.Enabled;
        agent.MaxConcurrentRuns = command.MaxConcurrentRuns;
        agent.MonthlyBudgetUsd = command.MonthlyBudgetUsd;
        agent.Settings = command.Settings ?? agent.Settings;
        db.Audit(user, project.OrganizationId, command.AgentId is null ? "agent.created" : "agent.updated", "agent", agent.Id,
            new { agent.Name, agent.Provider, agent.Runtime, agent.Model, agent.Enabled, agent.PairedUniqueName }, project.Id);
        await db.SaveChangesAsync(ct);
        var messages = await dispatcher.QueueAssignedWorkAsync(project, ct);
        await db.SaveChangesAsync(ct);
        return ((await AgentQueries.ListAsync(db, clock, project.Id, agent.Id, ct)).Single(), messages);
    }
}

public sealed record DeleteAgentCommand(Guid ProjectId, Guid AgentId);

public static class DeleteAgentHandler
{
    public static async Task Handle(DeleteAgentCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.AgentsManage, ct);
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == command.AgentId && a.ProjectId == command.ProjectId, ct) ?? throw new NotFoundException("Agent");
        if (await db.AgentRuns.AnyAsync(r => r.AgentId == agent.Id && r.Status == RunStatus.Running, ct))
            throw new ConflictException("Stop the agent's running jobs first (disable the agent)");
        db.Agents.Remove(agent);
        db.Audit(user, agent.OrganizationId, "agent.deleted", "agent", agent.Id, new { agent.Name }, command.ProjectId);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Creates one developer agent for every team member who does not have one.</summary>
public sealed record ProvisionTeamAgentsCommand(Guid ProjectId, Guid TeamId, ModelProvider Provider, string Model, IReadOnlyList<string>? SkillKeys,
    AgentRuntime Runtime = AgentRuntime.Framework);

public sealed class ProvisionTeamAgentsCommandValidator : AbstractValidator<ProvisionTeamAgentsCommand>
{
    public ProvisionTeamAgentsCommandValidator()
    {
        RuleFor(x => x.Provider).IsInEnum().NotEqual(ModelProvider.AzureOpenAI).WithMessage("Provision Azure OpenAI agents individually (they need an endpoint)");
        RuleFor(x => x.Model).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Runtime).IsInEnum();
        RuleFor(x => x).Must(x => RuntimeRules.Compatible(x.Runtime, x.Provider)).WithName("Runtime").WithMessage(RuntimeRules.Message);
    }
}

public static class ProvisionTeamAgentsHandler
{
    public static async Task<(int, IEnumerable<object>)> Handle(ProvisionTeamAgentsCommand command, IAppDbContext db, AccessControl access, ICurrentUser user,
        Dispatcher dispatcher, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.AgentsManage, ct);
        var project = await db.Projects.FirstAsync(p => p.Id == command.ProjectId, ct);
        var team = await db.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == command.TeamId && t.ProjectId == project.Id, ct) ?? throw new NotFoundException("Team");
        var paired = (await db.Agents.Where(a => a.ProjectId == project.Id && a.PairedUniqueName != null).Select(a => a.PairedUniqueName!).ToListAsync(ct)).ToHashSet();
        var skillIds = command.SkillKeys is { Count: > 0 } keys ? await db.Skills.Where(s => keys.Contains(s.Key)).Select(s => s.Id).ToListAsync(ct) : [];
        var created = 0;
        foreach (var member in team.Members.Where(m => !paired.Contains(m.UniqueName)))
        {
            var agent = new Agent
            {
                OrganizationId = project.OrganizationId, ProjectId = project.Id, Name = $"{member.DisplayName} (agent)", Kind = AgentKind.Developer,
                Provider = command.Provider, Runtime = command.Runtime, Model = command.Model, PairedUniqueName = member.UniqueName, TeamId = team.Id,
            };
            agent.Skills.AddRange(skillIds.Select(id => new AgentSkill { AgentId = agent.Id, SkillId = id }));
            db.Agents.Add(agent);
            created++;
        }
        db.Audit(user, project.OrganizationId, "agents.provisioned", "team", team.Id, new { created, command.Provider, command.Runtime, command.Model }, project.Id);
        await db.SaveChangesAsync(ct);
        var messages = await dispatcher.QueueAssignedWorkAsync(project, ct);
        await db.SaveChangesAsync(ct);
        return (created, messages);
    }
}

public sealed record SetAgentCapabilitiesCommand(Guid ProjectId, Guid AgentId, IReadOnlyList<string> SkillKeys, IReadOnlyList<string> McpServerNames);

public static class SetAgentCapabilitiesHandler
{
    public static async Task Handle(SetAgentCapabilitiesCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.AgentsManage, ct);
        var agent = await db.Agents.Include(a => a.Skills).Include(a => a.McpServers)
            .FirstOrDefaultAsync(a => a.Id == command.AgentId && a.ProjectId == command.ProjectId, ct) ?? throw new NotFoundException("Agent");
        var skills = await db.Skills.Where(s => command.SkillKeys.Contains(s.Key)).ToListAsync(ct);
        var servers = await db.McpServers.Where(s => command.McpServerNames.Contains(s.Name)).ToListAsync(ct);
        var unknown = command.SkillKeys.Except(skills.Select(s => s.Key)).Concat(command.McpServerNames.Except(servers.Select(s => s.Name))).ToList();
        if (unknown.Count > 0) throw new BusinessRuleException($"Unknown skills or MCP servers: {string.Join(", ", unknown)}");
        agent.Skills.Clear();
        agent.Skills.AddRange(skills.Select(s => new AgentSkill { AgentId = agent.Id, SkillId = s.Id }));
        agent.McpServers.Clear();
        agent.McpServers.AddRange(servers.Select(s => new AgentMcpServer { AgentId = agent.Id, McpServerId = s.Id }));
        db.Audit(user, agent.OrganizationId, "agent.capabilities.updated", "agent", agent.Id, command, command.ProjectId);
        await db.SaveChangesAsync(ct);
    }
}

// Skills -----------------------------------------------------------------------------------------

public sealed record SkillDto(Guid Id, string Key, string Name, string Description, string Content);

public sealed record ListSkillsQuery;

public static class ListSkillsHandler
{
    public static async Task<IReadOnlyList<SkillDto>> Handle(ListSkillsQuery _, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        if (await access.OrganizationRoleAsync(ct) is null) throw new ForbiddenException();
        return await db.Skills.AsNoTracking().OrderBy(s => s.Key).Select(s => new SkillDto(s.Id, s.Key, s.Name, s.Description, s.Content)).ToListAsync(ct);
    }
}

public sealed record SaveSkillCommand(string Key, string Name, string? Description, string Content);

public sealed class SaveSkillCommandValidator : AbstractValidator<SaveSkillCommand>
{
    public SaveSkillCommandValidator()
    {
        RuleFor(x => x.Key).NotEmpty().Matches("^[a-z0-9][a-z0-9-]{0,59}$").WithMessage("Key: lowercase letters, digits and dashes");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.Content).NotEmpty().MaximumLength(50_000);
    }
}

public static class SaveSkillHandler
{
    public static async Task<SkillDto> Handle(SaveSkillCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        var organizationId = access.RequireOrganization();
        var skill = await db.Skills.FirstOrDefaultAsync(s => s.Key == command.Key, ct);
        if (skill is null)
        {
            skill = new Skill { OrganizationId = organizationId, Key = command.Key, Name = command.Name, Content = command.Content };
            db.Skills.Add(skill);
        }
        skill.Name = command.Name.Trim();
        skill.Description = command.Description ?? "";
        skill.Content = command.Content;
        db.Audit(user, organizationId, "skill.saved", "skill", command.Key);
        await db.SaveChangesAsync(ct);
        return new SkillDto(skill.Id, skill.Key, skill.Name, skill.Description, skill.Content);
    }
}

public sealed record DeleteSkillCommand(string Key);

public static class DeleteSkillHandler
{
    public static async Task Handle(DeleteSkillCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        var skill = await db.Skills.FirstOrDefaultAsync(s => s.Key == command.Key, ct) ?? throw new NotFoundException("Skill");
        db.Skills.Remove(skill);
        db.Audit(user, skill.OrganizationId, "skill.deleted", "skill", command.Key);
        await db.SaveChangesAsync(ct);
    }
}

public sealed record SkillSuggestionDto(string Key, string Name, string Description, string Reason);

public sealed record SuggestSkillsQuery(Guid ProjectId);

public static class SuggestSkillsHandler
{
    public static async Task<IReadOnlyList<SkillSuggestionDto>> Handle(SuggestSkillsQuery query, IAppDbContext db, AccessControl access, AzureConnector connector, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.AgentsManage, ct);
        var installed = (await db.Skills.Select(s => s.Key).ToListAsync(ct)).ToHashSet();
        var (_, azure) = await connector.ConnectAsync(query.ProjectId, ct);
        var repos = await db.Repositories.AsNoTracking().Where(r => r.ProjectId == query.ProjectId && r.DefaultBranch != null).Select(r => r.Name).Take(50).ToListAsync(ct);
        var files = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var repo in repos) files[repo] = await azure.ListRootFilesAsync(repo, ct);
        return SkillCatalog.Suggest(files, installed);
    }
}

public sealed record GetCatalogQuery;

public sealed record CatalogDto(IReadOnlyList<CatalogSkill> Skills, IReadOnlyList<CatalogMcpServer> McpServers);

public static class GetCatalogHandler
{
    public static CatalogDto Handle(GetCatalogQuery _) => new(SkillCatalog.Skills, SkillCatalog.McpServers);
}

// MCP servers ----------------------------------------------------------------------------------------

public sealed record McpServerDto(Guid Id, string Name, McpTransport Transport, string? Command, IReadOnlyList<string> Arguments, string? Url, IReadOnlyList<string> SecretNames);

public sealed record ListMcpServersQuery;

public static class ListMcpServersHandler
{
    public static async Task<IReadOnlyList<McpServerDto>> Handle(ListMcpServersQuery _, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        if (await access.OrganizationRoleAsync(ct) is null) throw new ForbiddenException();
        return await db.McpServers.AsNoTracking().OrderBy(s => s.Name)
            .Select(s => new McpServerDto(s.Id, s.Name, s.Transport, s.Command, s.Arguments, s.Url, s.SecretNames)).ToListAsync(ct);
    }
}

public sealed record SaveMcpServerCommand(string Name, McpTransport Transport, string? Command, IReadOnlyList<string>? Arguments, string? Url, IReadOnlyList<string>? SecretNames);

public sealed class SaveMcpServerCommandValidator : AbstractValidator<SaveMcpServerCommand>
{
    public SaveMcpServerCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().Matches("^[a-z0-9][a-z0-9_-]{0,59}$");
        RuleFor(x => x.Transport).IsInEnum();
        RuleFor(x => x.Command).NotEmpty().MaximumLength(200).When(x => x.Transport == McpTransport.Stdio);
        RuleFor(x => x.Url).NotEmpty().Must(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            .When(x => x.Transport == McpTransport.Http).WithMessage("A valid URL is required for HTTP servers");
        RuleForEach(x => x.Arguments).MaximumLength(500);
        RuleForEach(x => x.SecretNames).Matches("^[A-Z][A-Z0-9_]{1,79}$");
    }
}

public static class SaveMcpServerHandler
{
    public static async Task<McpServerDto> Handle(SaveMcpServerCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        var organizationId = access.RequireOrganization();
        var server = await db.McpServers.FirstOrDefaultAsync(s => s.Name == command.Name, ct);
        if (server is null)
        {
            server = new McpServer { OrganizationId = organizationId, Name = command.Name };
            db.McpServers.Add(server);
        }
        server.Transport = command.Transport;
        server.Command = command.Transport == McpTransport.Stdio ? command.Command : null;
        server.Arguments = command.Arguments?.ToList() ?? [];
        server.Url = command.Transport == McpTransport.Http ? command.Url : null;
        server.SecretNames = command.SecretNames?.ToList() ?? [];
        db.Audit(user, organizationId, "mcp_server.saved", "mcp_server", command.Name);
        await db.SaveChangesAsync(ct);
        return new McpServerDto(server.Id, server.Name, server.Transport, server.Command, server.Arguments, server.Url, server.SecretNames);
    }
}

public sealed record DeleteMcpServerCommand(string Name);

public static class DeleteMcpServerHandler
{
    public static async Task Handle(DeleteMcpServerCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        var server = await db.McpServers.FirstOrDefaultAsync(s => s.Name == command.Name, ct) ?? throw new NotFoundException("MCP server");
        db.McpServers.Remove(server);
        db.Audit(user, server.OrganizationId, "mcp_server.deleted", "mcp_server", command.Name);
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
