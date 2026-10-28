using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Domain.Azure;
using AzureAgent.Domain.Projects;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Features.Projects;

public sealed record ProjectDto(Guid Id, string Name, string Key, string? Description, string AzureProjectName, string WebUrl, DateTimeOffset? LastSyncedAt,
    string? LastSyncError, ProjectRole? MyRole, IReadOnlyCollection<string> MyPermissions, int OpenWorkItems, int AssignedToMe, int Repositories, int Teams,
    decimal? MonthlyBudgetUsd);

public sealed class AzureUrlValidator : AbstractValidator<string>
{
    public AzureUrlValidator()
    {
        RuleFor(x => x).NotEmpty()
            .Matches(@"^https://(dev\.azure\.com/[A-Za-z0-9._-]+|[A-Za-z0-9-]+\.visualstudio\.com)/?$")
            .WithMessage("Use https://dev.azure.com/<organization> or https://<organization>.visualstudio.com");
    }
}

internal static class ProjectQueries
{
    public static async Task<IReadOnlyList<ProjectDto>> ListAsync(IAppDbContext db, AccessControl access, ICurrentUser user, Guid? projectId, CancellationToken ct)
    {
        var userId = access.RequireUser();
        var projects = await db.Projects.AsNoTracking().Where(p => !p.IsRemoved && (projectId == null || p.Id == projectId)).OrderBy(p => p.Name).ToListAsync(ct);
        var roles = await db.ProjectMembers.AsNoTracking().Where(m => m.UserId == userId).ToDictionaryAsync(m => m.ProjectId, ct);
        var ids = projects.Select(p => p.Id).ToList();
        var items = await db.WorkItems.AsNoTracking().Where(w => ids.Contains(w.ProjectId))
            .Select(w => new { w.ProjectId, w.Type, w.State, w.AssignedToUniqueName }).ToListAsync(ct);
        var repos = await db.Repositories.AsNoTracking().Where(r => ids.Contains(r.ProjectId) && !r.IsRemoved).GroupBy(r => r.ProjectId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var teams = await db.Teams.AsNoTracking().Where(t => ids.Contains(t.ProjectId)).GroupBy(t => t.ProjectId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var result = new List<ProjectDto>();
        foreach (var project in projects)
        {
            var permissions = await access.PermissionsAsync(project.Id, null, ct);
            if (!permissions.Contains(Permission.ProjectView)) continue;
            var member = roles.GetValueOrDefault(project.Id);
            var me = member?.AzureUniqueName ?? user.Email;
            var open = items.Where(i => i.ProjectId == project.Id && !project.IsClosedState(i.Type, i.State)).ToList();
            result.Add(new ProjectDto(project.Id, project.Name, project.Key, project.Description, project.AzureProjectName,
                $"{project.AzureOrganizationUrl.TrimEnd('/')}/{Uri.EscapeDataString(project.AzureProjectName)}", project.LastSyncedAt, project.LastSyncError,
                member?.Role, permissions.Order().ToList(), open.Count, open.Count(i => i.AssignedToUniqueName == me), repos.GetValueOrDefault(project.Id),
                teams.GetValueOrDefault(project.Id), project.MonthlyBudgetUsd));
        }
        return result;
    }
}

/// <summary>The synced Azure DevOps projects the caller can see.</summary>
public sealed record ListProjectsQuery;

public static class ListProjectsHandler
{
    public static Task<IReadOnlyList<ProjectDto>> Handle(ListProjectsQuery _, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        access.RequireOrganization();
        return ProjectQueries.ListAsync(db, access, user, null, ct);
    }
}

public sealed record GetProjectQuery(Guid ProjectId);

public static class GetProjectHandler
{
    public static async Task<ProjectDto> Handle(GetProjectQuery query, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.ProjectView, ct);
        return (await ProjectQueries.ListAsync(db, access, user, query.ProjectId, ct)).SingleOrDefault() ?? throw new NotFoundException("Project");
    }
}

// Project roles (managed from Settings → Users) ------------------------------------------------------------

public sealed record SetProjectMemberCommand(Guid ProjectId, Guid UserId, ProjectRole Role, string? AzureUniqueName);

public sealed class SetProjectMemberCommandValidator : AbstractValidator<SetProjectMemberCommand>
{
    public SetProjectMemberCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.Role).IsInEnum();
        RuleFor(x => x.AzureUniqueName).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrEmpty(x.AzureUniqueName));
    }
}

public static class SetProjectMemberHandler
{
    public static async Task Handle(SetProjectMemberCommand command, IAppDbContext db, AccessControl access, ICurrentUser current, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.MembersManage, ct);
        var organizationId = access.RequireOrganization();
        if (!await db.OrganizationMembers.AnyAsync(m => m.UserId == command.UserId, ct))
            throw new BusinessRuleException("Add the user to the organization first");
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == command.UserId, ct);
        var azureName = string.IsNullOrWhiteSpace(command.AzureUniqueName) ? user.Email : command.AzureUniqueName.Trim().ToLowerInvariant();
        var member = await db.ProjectMembers.FirstOrDefaultAsync(m => m.ProjectId == command.ProjectId && m.UserId == command.UserId, ct);
        if (member is null)
        {
            db.ProjectMembers.Add(new ProjectMember
            {
                OrganizationId = organizationId, ProjectId = command.ProjectId, UserId = command.UserId, Role = command.Role, AzureUniqueName = azureName,
            });
        }
        else
        {
            member.Role = command.Role;
            member.AzureUniqueName = azureName;
            // An explicit decision pins the membership: team sync no longer changes or removes it.
            member.Source = MembershipSource.Manual;
        }
        db.Audit(current, organizationId, "project.member.saved", "user", command.UserId, new { command.Role, azureName }, command.ProjectId);
        await db.SaveChangesAsync(ct);
    }
}

public sealed record RemoveProjectMemberCommand(Guid ProjectId, Guid UserId);

public static class RemoveProjectMemberHandler
{
    public static async Task Handle(RemoveProjectMemberCommand command, IAppDbContext db, AccessControl access, ICurrentUser current, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.MembersManage, ct);
        var member = await db.ProjectMembers.FirstOrDefaultAsync(m => m.ProjectId == command.ProjectId && m.UserId == command.UserId, ct)
            ?? throw new NotFoundException("Project member");
        db.ProjectMembers.Remove(member);
        db.Audit(current, member.OrganizationId, "project.member.removed", "user", command.UserId, null, command.ProjectId);
        await db.SaveChangesAsync(ct);
    }
}

// Repository security (Settings → Repositories) -----------------------------------------------------------

public sealed record UpdateRepositoryPolicyCommand(Guid RepositoryId, RepositoryPolicy Policy);

public sealed class UpdateRepositoryPolicyCommandValidator : AbstractValidator<UpdateRepositoryPolicyCommand>
{
    public UpdateRepositoryPolicyCommandValidator()
    {
        RuleFor(x => x.Policy).NotNull();
        RuleFor(x => x.Policy.MaxFilesChangedPerRun).InclusiveBetween(1, 1000);
        RuleForEach(x => x.Policy.ProtectedBranches).NotEmpty().MaximumLength(200);
        RuleForEach(x => x.Policy.AllowedCommands).NotEmpty().Matches("^[A-Za-z0-9._-]+$").WithMessage("Commands are bare executable names");
    }
}

public static class UpdateRepositoryPolicyHandler
{
    public static async Task Handle(UpdateRepositoryPolicyCommand command, IAppDbContext db, AccessControl access, ICurrentUser current, CancellationToken ct)
    {
        var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Id == command.RepositoryId, ct) ?? throw new NotFoundException("Repository");
        await access.RequireAsync(repo.ProjectId, Permission.RepositoryPolicyManage, ct, repo.Id);
        repo.Policy = command.Policy;
        db.Audit(current, repo.OrganizationId, "repository.policy.updated", "repository", repo.Id, command.Policy, repo.ProjectId);
        await db.SaveChangesAsync(ct);
    }
}

public sealed record RepositoryAccessRuleDto(Guid Id, Guid? UserId, string? UserName, ProjectRole? Role, string Permission, AccessEffect Effect);

public sealed record ListRepositoryAccessRulesQuery(Guid RepositoryId);

public static class ListRepositoryAccessRulesHandler
{
    public static async Task<IReadOnlyList<RepositoryAccessRuleDto>> Handle(ListRepositoryAccessRulesQuery query, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        var repo = await db.Repositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == query.RepositoryId, ct) ?? throw new NotFoundException("Repository");
        await access.RequireAsync(repo.ProjectId, Permission.RepositoryPolicyManage, ct, repo.Id);
        return await db.RepositoryAccessRules.AsNoTracking().Where(r => r.RepositoryId == query.RepositoryId)
            .Select(r => new RepositoryAccessRuleDto(r.Id, r.UserId, db.Users.Where(u => u.Id == r.UserId).Select(u => u.DisplayName).FirstOrDefault(), r.Role,
                r.Permission, r.Effect)).ToListAsync(ct);
    }
}

public sealed record AddRepositoryAccessRuleCommand(Guid RepositoryId, Guid? UserId, ProjectRole? Role, string Permission, AccessEffect Effect);

public sealed class AddRepositoryAccessRuleCommandValidator : AbstractValidator<AddRepositoryAccessRuleCommand>
{
    public AddRepositoryAccessRuleCommandValidator()
    {
        RuleFor(x => x).Must(x => x.UserId.HasValue ^ x.Role.HasValue).WithMessage("Target either a user or a role");
        RuleFor(x => x.Permission).Must(p => Domain.Projects.Permission.RepositoryScoped.Contains(p))
            .WithMessage($"Permission must be one of: {string.Join(", ", Domain.Projects.Permission.RepositoryScoped)}");
        RuleFor(x => x.Effect).IsInEnum();
    }
}

public static class AddRepositoryAccessRuleHandler
{
    public static async Task<RepositoryAccessRuleDto> Handle(AddRepositoryAccessRuleCommand command, IAppDbContext db, AccessControl access, ICurrentUser current, CancellationToken ct)
    {
        var repo = await db.Repositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == command.RepositoryId, ct) ?? throw new NotFoundException("Repository");
        await access.RequireAsync(repo.ProjectId, Permission.RepositoryPolicyManage, ct, repo.Id);
        var rule = new RepositoryAccessRule
        {
            OrganizationId = repo.OrganizationId, RepositoryId = repo.Id, UserId = command.UserId, Role = command.Role,
            Permission = command.Permission, Effect = command.Effect,
        };
        db.RepositoryAccessRules.Add(rule);
        db.Audit(current, repo.OrganizationId, "repository.access_rule.added", "repository", repo.Id, command, repo.ProjectId);
        await db.SaveChangesAsync(ct);
        var name = command.UserId is { } id ? await db.Users.Where(u => u.Id == id).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) : null;
        return new RepositoryAccessRuleDto(rule.Id, rule.UserId, name, rule.Role, rule.Permission, rule.Effect);
    }
}

public sealed record RemoveRepositoryAccessRuleCommand(Guid RepositoryId, Guid RuleId);

public static class RemoveRepositoryAccessRuleHandler
{
    public static async Task Handle(RemoveRepositoryAccessRuleCommand command, IAppDbContext db, AccessControl access, ICurrentUser current, CancellationToken ct)
    {
        var rule = await db.RepositoryAccessRules.FirstOrDefaultAsync(r => r.Id == command.RuleId && r.RepositoryId == command.RepositoryId, ct)
            ?? throw new NotFoundException("Access rule");
        var repo = await db.Repositories.AsNoTracking().FirstAsync(r => r.Id == rule.RepositoryId, ct);
        await access.RequireAsync(repo.ProjectId, Permission.RepositoryPolicyManage, ct, repo.Id);
        db.RepositoryAccessRules.Remove(rule);
        db.Audit(current, rule.OrganizationId, "repository.access_rule.removed", "repository", rule.RepositoryId, new { rule.Permission }, repo.ProjectId);
        await db.SaveChangesAsync(ct);
    }
}
