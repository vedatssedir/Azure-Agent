using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Domain.Agents;
using AzureAgent.Domain.Azure;
using AzureAgent.Domain.Identity;
using AzureAgent.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzureAgent.Application.Features.Identity;

public sealed record ProvisioningResult(int Added, int Updated, int Removed, int Invited);

/// <summary>
/// Keeps project memberships in line with Azure DevOps: people in the project's Azure teams (and,
/// for Entra ID sign-ins, mapped groups) get a membership with the mapped role. Memberships created
/// this way are removed when the person leaves every mapped team; manual memberships are never touched.
/// </summary>
public sealed class MembershipProvisioner(IAppDbContext db, ITenantContext tenant, InvitationService invitations, ILogger<MembershipProvisioner> logger)
{
    /// <summary>Azure uniqueName → project role, from the team mappings (highest role wins).</summary>
    public static Dictionary<string, ProjectRole> DesiredRoles(OrganizationSettings settings, IEnumerable<Team> teams)
    {
        var result = new Dictionary<string, ProjectRole>(StringComparer.OrdinalIgnoreCase);
        foreach (var team in teams)
        {
            ProjectRole role;
            if (settings.TeamRoles.Count == 0) role = ProjectRole.Developer;
            else if (settings.TeamRoles.FirstOrDefault(m => m.Team.Equals(team.Name, StringComparison.OrdinalIgnoreCase)) is { } mapping) role = mapping.Role;
            else continue;
            foreach (var member in team.Members.Where(m => m.UniqueName.Length > 0))
            {
                var name = member.UniqueName.ToLowerInvariant();
                if (!result.TryGetValue(name, out var current) || role > current) result[name] = role;
            }
        }
        return result;
    }

    public static ProjectRole? GroupRole(OrganizationSettings settings, IReadOnlyCollection<string> groups) =>
        settings.GroupRoles.Where(g => groups.Contains(g.GroupId, StringComparer.OrdinalIgnoreCase))
            .Select(g => (ProjectRole?)g.Role).DefaultIfEmpty(null).Max();

    /// <summary>Reconciles one project's memberships with its synced Azure teams. Runs in the project's tenant.</summary>
    public async Task<ProvisioningResult> SyncProjectAsync(Project project, string actor, CancellationToken ct)
    {
        var settings = await db.Organizations.AsNoTracking().Where(o => o.Id == project.OrganizationId).Select(o => o.Settings).FirstAsync(ct);
        if (!settings.AutoProvisionMembers) return new ProvisioningResult(0, 0, 0, 0);
        var teams = await db.Teams.Where(t => t.ProjectId == project.Id).ToListAsync(ct);
        var desired = DesiredRoles(settings, teams);
        var names = desired.Keys.ToList();
        var users = await db.Users.Where(u => u.IsActive && names.Contains(u.Email)).ToListAsync(ct);
        var members = await db.ProjectMembers.Where(m => m.ProjectId == project.Id).ToListAsync(ct);
        var orgMembers = (await db.OrganizationMembers.Where(m => m.OrganizationId == project.OrganizationId).Select(m => m.UserId).ToListAsync(ct)).ToHashSet();
        int added = 0, updated = 0, removed = 0, invited = 0;

        foreach (var user in users)
        {
            var role = desired[user.Email];
            if (orgMembers.Add(user.Id))
                db.OrganizationMembers.Add(new OrganizationMember { OrganizationId = project.OrganizationId, UserId = user.Id, Role = OrganizationRole.Member });
            var member = members.FirstOrDefault(m => m.UserId == user.Id);
            if (member is null)
            {
                db.ProjectMembers.Add(new ProjectMember
                {
                    OrganizationId = project.OrganizationId, ProjectId = project.Id, UserId = user.Id, Role = role, AzureUniqueName = user.Email,
                    Source = MembershipSource.AzureTeam,
                });
                added++;
            }
            else if (member.Source == MembershipSource.AzureTeam && (member.Role != role || member.AzureUniqueName != user.Email))
            {
                member.Role = role;
                member.AzureUniqueName = user.Email;
                updated++;
            }
            else if (member.AzureUniqueName is null)
            {
                member.AzureUniqueName = user.Email;
                updated++;
            }
        }
        foreach (var member in members.Where(m => m.Source == MembershipSource.AzureTeam && (m.AzureUniqueName is null || !desired.ContainsKey(m.AzureUniqueName))))
        {
            db.ProjectMembers.Remove(member);
            removed++;
        }

        if (settings.AutoInviteTeamMembers)
        {
            var known = users.Select(u => u.Email).ToHashSet();
            var displayNames = teams.SelectMany(t => t.Members).GroupBy(m => m.UniqueName.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().DisplayName);
            foreach (var name in names.Where(n => !known.Contains(n) && InvitationService.LooksLikeEmail(n)))
            {
                if (await invitations.CreateIfMissingAsync(project.OrganizationId, name, displayNames.GetValueOrDefault(name) ?? name, OrganizationRole.Member,
                        project.Id, actor, sendEmail: true, ct) is not null) invited++;
            }
        }
        await EnsureDefaultAgentsAsync(project.OrganizationId, settings, users.Where(u => desired[u.Email] >= ProjectRole.Developer).ToList(), ct);
        if (added + updated + removed + invited > 0)
        {
            db.AuditEntries.Add(new Domain.Auditing.AuditEntry
            {
                OrganizationId = project.OrganizationId, ProjectId = project.Id, Actor = actor, Action = "project.members.provisioned",
                EntityType = "project", EntityId = project.Id.ToString(),
                Details = System.Text.Json.JsonSerializer.Serialize(new { added, updated, removed, invited }),
            });
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Provisioned members of {Project}: +{Added} ~{Updated} -{Removed}, {Invited} invited", project.Key, added, updated, removed, invited);
        return new ProvisioningResult(added, updated, removed, invited);
    }

    /// <summary>
    /// Gives a user (on sign-in or invitation acceptance) every membership the Azure teams and group
    /// mappings entitle them to, across organizations. Returns whether the user has access anywhere.
    /// </summary>
    public async Task<bool> ProvisionUserAsync(User user, IReadOnlyCollection<string> groups, CancellationToken ct)
    {
        tenant.UseAllOrganizations();
        var projects = await db.Projects.Where(p => !p.IsRemoved).ToListAsync(ct);
        var settingsByOrg = await db.Organizations.ToDictionaryAsync(o => o.Id, o => o.Settings, ct);
        var developerIn = new HashSet<Guid>();
        var teams = (await db.Teams.ToListAsync(ct)).ToLookup(t => t.ProjectId);
        var members = await db.ProjectMembers.Where(m => m.UserId == user.Id).ToListAsync(ct);
        var orgMembers = (await db.OrganizationMembers.Where(m => m.UserId == user.Id).Select(m => m.OrganizationId).ToListAsync(ct)).ToHashSet();
        foreach (var project in projects)
        {
            var settings = settingsByOrg[project.OrganizationId];
            var teamRole = settings.AutoProvisionMembers && DesiredRoles(settings, teams[project.Id]).TryGetValue(user.Email, out var r) ? r : (ProjectRole?)null;
            var groupRole = GroupRole(settings, groups);
            var existing = members.FirstOrDefault(m => m.ProjectId == project.Id);
            if (teamRole is null && groupRole is null)
            {
                // Left the Entra group: drop the membership it granted.
                if (existing is { Source: MembershipSource.EntraGroup }) db.ProjectMembers.Remove(existing);
                continue;
            }
            var role = (ProjectRole)Math.Max((int)(teamRole ?? ProjectRole.Viewer), (int)(groupRole ?? ProjectRole.Viewer));
            if (role >= ProjectRole.Developer) developerIn.Add(project.OrganizationId);
            var source = teamRole >= (groupRole ?? ProjectRole.Viewer) ? MembershipSource.AzureTeam : MembershipSource.EntraGroup;
            if (orgMembers.Add(project.OrganizationId))
                db.OrganizationMembers.Add(new OrganizationMember { OrganizationId = project.OrganizationId, UserId = user.Id, Role = OrganizationRole.Member });
            var member = existing;
            if (member is null)
            {
                member = new ProjectMember
                {
                    OrganizationId = project.OrganizationId, ProjectId = project.Id, UserId = user.Id, Role = role, AzureUniqueName = user.Email, Source = source,
                };
                db.ProjectMembers.Add(member);
                members.Add(member);
                logger.LogInformation("Provisioned {Email} into {Project} as {Role} ({Source})", user.Email, project.Key, role, source);
            }
            else if (member.Source != MembershipSource.Manual)
            {
                member.Role = role;
                member.Source = source;
                member.AzureUniqueName ??= user.Email;
            }
        }
        await db.SaveChangesAsync(ct);
        foreach (var organizationId in developerIn) await EnsureDefaultAgentsAsync(organizationId, settingsByOrg[organizationId], [user], ct);
        return orgMembers.Count > 0;
    }

    /// <summary>Gives developers without any agent a default one built from the organization's AI defaults.</summary>
    private async Task EnsureDefaultAgentsAsync(Guid organizationId, OrganizationSettings settings, IReadOnlyCollection<User> developers, CancellationToken ct)
    {
        if (!settings.CreateDefaultAgentForDevelopers || developers.Count == 0) return;
        var ids = developers.Select(u => u.Id).ToList();
        var owners = (await db.Agents.Where(a => a.OrganizationId == organizationId && a.OwnerUserId != null && ids.Contains(a.OwnerUserId.Value))
            .Select(a => a.OwnerUserId!.Value).ToListAsync(ct)).ToHashSet();
        var slugs = (await db.Agents.Where(a => a.OrganizationId == organizationId).Select(a => a.Slug).ToListAsync(ct)).ToHashSet();
        foreach (var user in developers.Where(u => !owners.Contains(u.Id)))
        {
            var name = $"{user.DisplayName.Split(' ')[0]}'s agent";
            db.Agents.Add(new Agent
            {
                OrganizationId = organizationId, OwnerUserId = user.Id, Name = name, Slug = AgentSlugs.Unique(name, slugs), Title = "Developer agent",
                Kind = AgentKind.Developer, Runtime = settings.DefaultRuntime, Provider = settings.DefaultProvider, Model = settings.DefaultModel, IsDefault = true,
            });
            logger.LogInformation("Created default agent for {Email}", user.Email);
        }
        await db.SaveChangesAsync(ct);
    }
}

public static class AgentSlugs
{
    public static string Unique(string name, ISet<string> taken)
    {
        var slug = Slugs.From(name, 40);
        var candidate = slug;
        for (var i = 2; taken.Contains(candidate); i++) candidate = $"{slug}-{i}";
        taken.Add(candidate);
        return candidate;
    }
}
