using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Application.Features.Identity;
using AzureAgent.Domain.Identity;
using AzureAgent.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Features.Settings;

public sealed record UserProjectRoleDto(Guid ProjectId, string ProjectName, ProjectRole Role, MembershipSource Source);

public sealed record OrganizationUserDto(Guid UserId, string Email, string DisplayName, OrganizationRole Role, bool IsActive, DateTimeOffset? LastLoginAt,
    bool AzureConnected, bool SignsInWithMicrosoft, int Agents, IReadOnlyList<UserProjectRoleDto> Projects);

/// <summary>Someone in the Azure DevOps teams who has no platform account yet.</summary>
public sealed record AzurePersonDto(string UniqueName, string DisplayName, IReadOnlyList<string> Projects, string? InvitationStatus);

public sealed record UsersDto(IReadOnlyList<OrganizationUserDto> Users, IReadOnlyList<AzurePersonDto> WithoutAccount, IReadOnlyList<InvitationDto> Invitations);

/// <summary>Everyone: platform users with their organization and project roles, Azure team members without an account, invitations.</summary>
public sealed record ListUsersQuery;

public static class ListUsersHandler
{
    public static async Task<UsersDto> Handle(ListUsersQuery _, IAppDbContext db, AccessControl access, IClock clock, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        var members = await db.OrganizationMembers.AsNoTracking().Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m, u }).ToListAsync(ct);
        var userIds = members.Select(x => x.u.Id).ToList();
        var projects = await db.Projects.AsNoTracking().Where(p => !p.IsRemoved).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var roles = (await db.ProjectMembers.AsNoTracking().ToListAsync(ct)).Where(m => projects.ContainsKey(m.ProjectId)).ToLookup(m => m.UserId);
        var connected = (await db.UserAzureCredentials.AsNoTracking().Where(c => userIds.Contains(c.UserId)).Select(c => c.UserId).ToListAsync(ct)).ToHashSet();
        var microsoft = (await db.ExternalLogins.AsNoTracking().Where(l => userIds.Contains(l.UserId)).Select(l => l.UserId).ToListAsync(ct)).ToHashSet();
        var agents = await db.Agents.AsNoTracking().Where(a => a.OwnerUserId != null).GroupBy(a => a.OwnerUserId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var users = members.OrderBy(x => x.u.DisplayName).Select(x => new OrganizationUserDto(x.u.Id, x.u.Email, x.u.DisplayName, x.m.Role, x.u.IsActive,
            x.u.LastLoginAt, connected.Contains(x.u.Id), microsoft.Contains(x.u.Id), agents.GetValueOrDefault(x.u.Id),
            roles[x.u.Id].OrderBy(r => projects[r.ProjectId]).Select(r => new UserProjectRoleDto(r.ProjectId, projects[r.ProjectId], r.Role, r.Source)).ToList())).ToList();

        var invitations = (await ListInvitationsHandler.Handle(new ListInvitationsQuery(), db, access, clock, ct));
        var emails = members.Select(x => x.u.Email).ToHashSet();
        var teams = await db.Teams.AsNoTracking().ToListAsync(ct);
        var withoutAccount = teams.Where(t => projects.ContainsKey(t.ProjectId))
            .SelectMany(t => t.Members.Select(m => (m.UniqueName, m.DisplayName, Project: projects[t.ProjectId])))
            .Where(m => !emails.Contains(m.UniqueName) && InvitationService.LooksLikeEmail(m.UniqueName))
            .GroupBy(m => m.UniqueName)
            .Select(g => new AzurePersonDto(g.Key, g.First().DisplayName, g.Select(x => x.Project).Distinct().Order().ToList(),
                invitations.FirstOrDefault(i => i.Email == g.Key)?.Status))
            .OrderBy(p => p.DisplayName).ToList();
        return new UsersDto(users, withoutAccount, invitations);
    }
}

public sealed record SetOrganizationRoleCommand(Guid UserId, OrganizationRole Role);

public static class SetOrganizationRoleHandler
{
    public static async Task Handle(SetOrganizationRoleCommand command, IAppDbContext db, AccessControl access, ICurrentUser current, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        var organizationId = access.RequireOrganization();
        var callerRole = await access.OrganizationRoleAsync(ct);
        var member = await db.OrganizationMembers.FirstOrDefaultAsync(m => m.UserId == command.UserId, ct) ?? throw new NotFoundException("Member");
        if ((command.Role == OrganizationRole.Owner || member.Role == OrganizationRole.Owner) && callerRole != OrganizationRole.Owner && !current.IsSystemAdmin)
            throw new ForbiddenException("Only owners can grant or remove the owner role");
        if (member.Role == OrganizationRole.Owner && command.Role != OrganizationRole.Owner
            && await db.OrganizationMembers.CountAsync(m => m.Role == OrganizationRole.Owner, ct) == 1)
            throw new BusinessRuleException("The organization needs at least one owner");
        member.Role = command.Role;
        db.Audit(current, organizationId, "organization.member.role", "user", command.UserId, new { command.Role });
        await db.SaveChangesAsync(ct);
    }
}
