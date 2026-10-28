using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Application.Features.Identity;
using AzureAgent.Domain.Identity;
using AzureAgent.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Features.Projects;

/// <summary>One person of the project's Azure teams and where they stand on the platform.</summary>
public sealed record ProjectPersonDto(
    string UniqueName,
    string DisplayName,
    IReadOnlyList<string> Teams,
    ProjectRole? MappedRole,
    Guid? UserId,
    ProjectRole? Role,
    MembershipSource? Source,
    bool AzureConnected,
    string? InvitationStatus,
    string? AgentName);

public sealed record ListProjectPeopleQuery(Guid ProjectId);

public static class ListProjectPeopleHandler
{
    public static async Task<IReadOnlyList<ProjectPersonDto>> Handle(ListProjectPeopleQuery query, IAppDbContext db, AccessControl access, IClock clock, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.ProjectView, ct);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == query.ProjectId, ct);
        var teams = await db.Teams.AsNoTracking().Where(t => t.ProjectId == project.Id).OrderBy(t => t.Name).ToListAsync(ct);
        var mapped = MembershipProvisioner.DesiredRoles(project.Settings, teams);
        var people = teams.SelectMany(t => t.Members.Select(m => (Team: t.Name, m.UniqueName, m.DisplayName)))
            .GroupBy(x => x.UniqueName.ToLowerInvariant())
            .Select(g => (UniqueName: g.Key, g.First().DisplayName, Teams: g.Select(x => x.Team).Distinct().ToList())).ToList();
        var names = people.Select(p => p.UniqueName).ToList();

        var users = await db.Users.AsNoTracking().Where(u => names.Contains(u.Email)).ToDictionaryAsync(u => u.Email, ct);
        var userIds = users.Values.Select(u => u.Id).ToList();
        var members = await db.ProjectMembers.AsNoTracking().Where(m => m.ProjectId == project.Id).ToListAsync(ct);
        var connected = (await db.UserAzureCredentials.AsNoTracking().Where(c => userIds.Contains(c.UserId)
                && (c.AzureOrganizationUrl == null || c.AzureOrganizationUrl == UserAzureCredentials.NormalizeUrl(project.AzureOrganizationUrl)))
            .Select(c => c.UserId).ToListAsync(ct)).ToHashSet();
        var now = clock.UtcNow;
        var invitations = (await db.Invitations.AsNoTracking().Where(i => names.Contains(i.Email)).OrderByDescending(i => i.CreatedAt).ToListAsync(ct))
            .GroupBy(i => i.Email).ToDictionary(g => g.Key, g => g.First());
        var agents = await db.Agents.AsNoTracking().Where(a => a.ProjectId == project.Id && a.PairedUniqueName != null)
            .ToDictionaryAsync(a => a.PairedUniqueName!, a => a.Name, ct);

        return people.OrderBy(p => p.DisplayName).Select(p =>
        {
            var user = users.GetValueOrDefault(p.UniqueName);
            var member = user is null ? null : members.FirstOrDefault(m => m.UserId == user.Id);
            var invitation = invitations.GetValueOrDefault(p.UniqueName);
            var invitationStatus = invitation is null ? null
                : invitation.AcceptedAt is not null ? "Accepted"
                : invitation.RevokedAt is not null ? "Revoked"
                : invitation.ExpiresAt <= now ? "Expired" : "Pending";
            return new ProjectPersonDto(p.UniqueName, p.DisplayName, p.Teams, mapped.TryGetValue(p.UniqueName, out var r) ? r : null,
                user?.Id, member?.Role, member?.Source, user is not null && connected.Contains(user.Id), invitationStatus, agents.GetValueOrDefault(p.UniqueName));
        }).ToList();
    }
}

/// <summary>Runs the Azure team → membership reconciliation now (it also runs on every sync).</summary>
public sealed record ProvisionProjectMembersCommand(Guid ProjectId);

public static class ProvisionProjectMembersHandler
{
    public static async Task<ProvisioningResult> Handle(ProvisionProjectMembersCommand command, IAppDbContext db, AccessControl access, ICurrentUser user,
        MembershipProvisioner provisioner, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.MembersManage, ct);
        var project = await db.Projects.FirstAsync(p => p.Id == command.ProjectId, ct);
        return await provisioner.SyncProjectAsync(project, user.ActorName, ct);
    }
}
