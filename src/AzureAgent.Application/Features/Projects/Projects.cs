using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Domain.Azure;
using AzureAgent.Domain.Projects;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Features.Projects;

public sealed record ProjectDto(Guid Id, string Name, string Key, string? Description, string AzureOrganizationUrl, string AzureProjectName,
    ProjectSettings Settings, decimal? MonthlyBudgetUsd, DateTimeOffset? LastSyncedAt, string? LastSyncError, IReadOnlyCollection<string> MyPermissions);

internal static class ProjectMapping
{
    public static ProjectDto ToDto(this Project p, IReadOnlySet<string> permissions) =>
        new(p.Id, p.Name, p.Key, p.Description, p.AzureOrganizationUrl, p.AzureProjectName, p.Settings, p.MonthlyBudgetUsd,
            p.LastSyncedAt, p.LastSyncError, permissions.Order().ToList());
}

public sealed class AzureUrlValidator : AbstractValidator<string>
{
    public AzureUrlValidator()
    {
        RuleFor(x => x).NotEmpty()
            .Matches(@"^https://(dev\.azure\.com/[A-Za-z0-9._-]+|[A-Za-z0-9-]+\.visualstudio\.com)/?$")
            .WithMessage("Use https://dev.azure.com/<organization> or https://<organization>.visualstudio.com");
    }
}

// Create ----------------------------------------------------------------------------------

public sealed record CreateProjectCommand(string Name, string Key, string? Description, string AzureOrganizationUrl, string AzureProjectName,
    decimal? MonthlyBudgetUsd);

public sealed class CreateProjectCommandValidator : AbstractValidator<CreateProjectCommand>
{
    public CreateProjectCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Key).NotEmpty().Matches("^[A-Z][A-Z0-9]{1,9}$").WithMessage("Key must be 2-10 uppercase letters or digits");
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.AzureOrganizationUrl).SetValidator(new AzureUrlValidator());
        RuleFor(x => x.AzureProjectName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.MonthlyBudgetUsd).GreaterThanOrEqualTo(0).When(x => x.MonthlyBudgetUsd.HasValue);
    }
}

public static class CreateProjectHandler
{
    public static async Task<ProjectDto> Handle(CreateProjectCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        var organizationId = access.RequireOrganization();
        if (await db.Projects.AnyAsync(p => p.Key == command.Key, ct)) throw new ConflictException($"Project key {command.Key} is already used");
        var project = new Project
        {
            OrganizationId = organizationId,
            Name = command.Name.Trim(),
            Key = command.Key,
            Description = command.Description,
            AzureOrganizationUrl = command.AzureOrganizationUrl.TrimEnd('/'),
            AzureProjectName = command.AzureProjectName.Trim(),
            MonthlyBudgetUsd = command.MonthlyBudgetUsd,
        };
        db.Projects.Add(project);
        db.Audit(user, organizationId, "project.created", "project", project.Id, new { project.Key }, project.Id);
        await db.SaveChangesAsync(ct);
        return project.ToDto(Permission.All.ToHashSet());
    }
}

// Read ------------------------------------------------------------------------------------

public sealed record ListProjectsQuery;

public static class ListProjectsHandler
{
    public static async Task<IReadOnlyList<ProjectDto>> Handle(ListProjectsQuery _, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        access.RequireOrganization();
        var result = new List<ProjectDto>();
        foreach (var project in await db.Projects.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct))
        {
            var permissions = await access.PermissionsAsync(project.Id, null, ct);
            if (permissions.Contains(Permission.ProjectView)) result.Add(project.ToDto(permissions));
        }
        return result;
    }
}

public sealed record GetProjectQuery(Guid ProjectId);

public static class GetProjectHandler
{
    public static async Task<ProjectDto> Handle(GetProjectQuery query, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.ProjectView, ct);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == query.ProjectId, ct);
        return project.ToDto(await access.PermissionsAsync(project.Id, null, ct));
    }
}

// Update ----------------------------------------------------------------------------------

public sealed record UpdateProjectCommand(Guid ProjectId, string Name, string? Description, string AzureOrganizationUrl, string AzureProjectName,
    decimal? MonthlyBudgetUsd, ProjectSettings Settings);

public sealed class UpdateProjectCommandValidator : AbstractValidator<UpdateProjectCommand>
{
    public UpdateProjectCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.AzureOrganizationUrl).SetValidator(new AzureUrlValidator());
        RuleFor(x => x.AzureProjectName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.MonthlyBudgetUsd).GreaterThanOrEqualTo(0).When(x => x.MonthlyBudgetUsd.HasValue);
        RuleFor(x => x.Settings).NotNull();
        RuleFor(x => x.Settings.SyncIntervalMinutes).InclusiveBetween(1, 1440);
        RuleFor(x => x.Settings.ReviewState).MaximumLength(50);
        RuleForEach(x => x.Settings.ClosedStates).NotEmpty().MaximumLength(50);
    }
}

public static class UpdateProjectHandler
{
    public static async Task<ProjectDto> Handle(UpdateProjectCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.SettingsManage, ct);
        var permissions = await access.PermissionsAsync(command.ProjectId, null, ct);
        var project = await db.Projects.FirstAsync(p => p.Id == command.ProjectId, ct);
        if (command.MonthlyBudgetUsd != project.MonthlyBudgetUsd && !permissions.Contains(Permission.BudgetsManage))
            throw new ForbiddenException($"Missing permission '{Permission.BudgetsManage}'");
        project.Name = command.Name.Trim();
        project.Description = command.Description;
        project.AzureOrganizationUrl = command.AzureOrganizationUrl.TrimEnd('/');
        project.AzureProjectName = command.AzureProjectName.Trim();
        project.MonthlyBudgetUsd = command.MonthlyBudgetUsd;
        project.Settings = command.Settings;
        db.Audit(user, project.OrganizationId, "project.updated", "project", project.Id, new { command.Settings, command.MonthlyBudgetUsd }, project.Id);
        await db.SaveChangesAsync(ct);
        return project.ToDto(permissions);
    }
}

// Members ---------------------------------------------------------------------------------

public sealed record ProjectMemberDto(Guid UserId, string Email, string DisplayName, ProjectRole Role, string? AzureUniqueName);

public sealed record ListProjectMembersQuery(Guid ProjectId);

public static class ListProjectMembersHandler
{
    public static async Task<IReadOnlyList<ProjectMemberDto>> Handle(ListProjectMembersQuery query, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.ProjectView, ct);
        return await db.ProjectMembers.AsNoTracking().Where(m => m.ProjectId == query.ProjectId)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m, u })
            .OrderBy(x => x.u.DisplayName)
            .Select(x => new ProjectMemberDto(x.u.Id, x.u.Email, x.u.DisplayName, x.m.Role, x.m.AzureUniqueName)).ToListAsync(ct);
    }
}

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
    public static async Task<ProjectMemberDto> Handle(SetProjectMemberCommand command, IAppDbContext db, AccessControl access, ICurrentUser current, CancellationToken ct)
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
            member = new ProjectMember { OrganizationId = organizationId, ProjectId = command.ProjectId, UserId = command.UserId, Role = command.Role, AzureUniqueName = azureName };
            db.ProjectMembers.Add(member);
        }
        else
        {
            member.Role = command.Role;
            member.AzureUniqueName = azureName;
        }
        db.Audit(current, organizationId, "project.member.saved", "user", command.UserId, new { command.Role, azureName }, command.ProjectId);
        await db.SaveChangesAsync(ct);
        return new ProjectMemberDto(user.Id, user.Email, user.DisplayName, command.Role, azureName);
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

// Repository security ---------------------------------------------------------------------------

public sealed record RepositoryDto(Guid Id, string Name, string? DefaultBranch, string WebUrl, RepositoryPolicy Policy, int OpenPullRequests, DateTimeOffset? LastCommitAt);

public sealed record ListRepositoriesQuery(Guid ProjectId);

public static class ListRepositoriesHandler
{
    public static async Task<IReadOnlyList<RepositoryDto>> Handle(ListRepositoriesQuery query, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.ProjectView, ct);
        var repos = await db.Repositories.AsNoTracking().Where(r => r.ProjectId == query.ProjectId).OrderBy(r => r.Name)
            .Select(r => new RepositoryDto(r.Id, r.Name, r.DefaultBranch, r.WebUrl, r.Policy,
                db.PullRequests.Count(p => p.RepositoryId == r.Id && p.Status == PullRequestStatus.Active),
                db.Commits.Where(c => c.RepositoryId == r.Id).Max(c => (DateTimeOffset?)c.CommittedAt)))
            .ToListAsync(ct);
        var visible = new List<RepositoryDto>();
        foreach (var repo in repos)
        {
            if ((await access.PermissionsAsync(query.ProjectId, repo.Id, ct)).Contains(Permission.RepositoryView)) visible.Add(repo);
        }
        return visible;
    }
}

public sealed record UpdateRepositoryPolicyCommand(Guid ProjectId, Guid RepositoryId, RepositoryPolicy Policy);

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
        await access.RequireAsync(command.ProjectId, Permission.RepositoryPolicyManage, ct, command.RepositoryId);
        var repo = await db.Repositories.FirstOrDefaultAsync(r => r.Id == command.RepositoryId && r.ProjectId == command.ProjectId, ct)
            ?? throw new NotFoundException("Repository");
        repo.Policy = command.Policy;
        db.Audit(current, repo.OrganizationId, "repository.policy.updated", "repository", repo.Id, command.Policy, command.ProjectId);
        await db.SaveChangesAsync(ct);
    }
}

public sealed record RepositoryAccessRuleDto(Guid Id, Guid? UserId, ProjectRole? Role, string Permission, AccessEffect Effect);

public sealed record ListRepositoryAccessRulesQuery(Guid ProjectId, Guid RepositoryId);

public static class ListRepositoryAccessRulesHandler
{
    public static async Task<IReadOnlyList<RepositoryAccessRuleDto>> Handle(ListRepositoryAccessRulesQuery query, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.RepositoryPolicyManage, ct, query.RepositoryId);
        return await db.RepositoryAccessRules.AsNoTracking().Where(r => r.RepositoryId == query.RepositoryId)
            .Select(r => new RepositoryAccessRuleDto(r.Id, r.UserId, r.Role, r.Permission, r.Effect)).ToListAsync(ct);
    }
}

public sealed record AddRepositoryAccessRuleCommand(Guid ProjectId, Guid RepositoryId, Guid? UserId, ProjectRole? Role, string Permission, AccessEffect Effect);

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
        await access.RequireAsync(command.ProjectId, Permission.RepositoryPolicyManage, ct, command.RepositoryId);
        var repo = await db.Repositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == command.RepositoryId && r.ProjectId == command.ProjectId, ct)
            ?? throw new NotFoundException("Repository");
        var rule = new RepositoryAccessRule
        {
            OrganizationId = repo.OrganizationId, RepositoryId = repo.Id, UserId = command.UserId, Role = command.Role,
            Permission = command.Permission, Effect = command.Effect,
        };
        db.RepositoryAccessRules.Add(rule);
        db.Audit(current, repo.OrganizationId, "repository.access_rule.added", "repository", repo.Id, command, command.ProjectId);
        await db.SaveChangesAsync(ct);
        return new RepositoryAccessRuleDto(rule.Id, rule.UserId, rule.Role, rule.Permission, rule.Effect);
    }
}

public sealed record RemoveRepositoryAccessRuleCommand(Guid ProjectId, Guid RepositoryId, Guid RuleId);

public static class RemoveRepositoryAccessRuleHandler
{
    public static async Task Handle(RemoveRepositoryAccessRuleCommand command, IAppDbContext db, AccessControl access, ICurrentUser current, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.RepositoryPolicyManage, ct, command.RepositoryId);
        var rule = await db.RepositoryAccessRules.FirstOrDefaultAsync(r => r.Id == command.RuleId && r.RepositoryId == command.RepositoryId, ct)
            ?? throw new NotFoundException("Access rule");
        db.RepositoryAccessRules.Remove(rule);
        db.Audit(current, rule.OrganizationId, "repository.access_rule.removed", "repository", rule.RepositoryId, new { rule.Permission }, command.ProjectId);
        await db.SaveChangesAsync(ct);
    }
}
