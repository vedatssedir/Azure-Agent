using AzureAgent.Application.Abstractions;
using AzureAgent.Domain.Azure;
using AzureAgent.Domain.Identity;
using AzureAgent.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Common;

/// <summary>
/// Resolves effective permissions: system admins and organization owners/admins hold every
/// permission; others get their project role's set, adjusted per repository by access rules
/// (deny wins over allow).
/// </summary>
public sealed class AccessControl(IAppDbContext db, ICurrentUser user, ITenantContext tenant)
{
    public Guid RequireUser() => user.UserId ?? throw new UnauthorizedException();

    public Guid RequireOrganization() =>
        tenant.OrganizationId ?? throw new BusinessRuleException("Select an organization (X-Organization-Id header)");

    public async Task<OrganizationRole?> OrganizationRoleAsync(CancellationToken ct)
    {
        var userId = RequireUser();
        var organizationId = RequireOrganization();
        var member = await db.OrganizationMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.OrganizationId == organizationId && m.UserId == userId, ct);
        return member?.Role;
    }

    public async Task RequireOrganizationAdminAsync(CancellationToken ct)
    {
        if (user.IsSystemAdmin) return;
        var role = await OrganizationRoleAsync(ct);
        if (role is not (OrganizationRole.Admin or OrganizationRole.Owner)) throw new ForbiddenException("Organization admin role required");
    }

    public async Task<IReadOnlySet<string>> PermissionsAsync(Guid projectId, Guid? repositoryId, CancellationToken ct)
    {
        var userId = RequireUser();
        if (!await db.Projects.AsNoTracking().AnyAsync(p => p.Id == projectId, ct)) throw new NotFoundException("Project");
        if (user.IsSystemAdmin || await OrganizationRoleAsync(ct) is OrganizationRole.Admin or OrganizationRole.Owner)
            return Permission.All.ToHashSet();

        var member = await db.ProjectMembers.AsNoTracking().FirstOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);
        if (member is null) return new HashSet<string>();
        var permissions = RolePermissions.For(member.Role).ToHashSet();
        if (repositoryId is null) return permissions;

        var rules = await db.RepositoryAccessRules.AsNoTracking()
            .Where(r => r.RepositoryId == repositoryId && (r.UserId == userId || r.Role == member.Role))
            .ToListAsync(ct);
        foreach (var rule in rules.Where(r => r.Effect == AccessEffect.Allow)) permissions.Add(rule.Permission);
        foreach (var rule in rules.Where(r => r.Effect == AccessEffect.Deny)) permissions.Remove(rule.Permission);
        return permissions;
    }

    public async Task RequireAsync(Guid projectId, string permission, CancellationToken ct, Guid? repositoryId = null)
    {
        var permissions = await PermissionsAsync(projectId, repositoryId, ct);
        if (!permissions.Contains(permission))
        {
            // Hide projects the caller cannot see at all.
            if (!permissions.Contains(Permission.ProjectView)) throw new NotFoundException("Project");
            throw new ForbiddenException($"Missing permission '{permission}'");
        }
    }
}
