using System.Net;
using System.Security.Cryptography;
using System.Text;
using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Application.Features.Auth;
using AzureAgent.Domain.Identity;
using AzureAgent.Domain.Projects;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzureAgent.Application.Features.Identity;

public sealed record InvitationLinkDto(Guid Id, string Email, string Url, bool EmailSent);

/// <summary>Creates invitations; the raw token only ever exists in the link handed out (and the optional e-mail).</summary>
public sealed class InvitationService(IAppDbContext db, IClock clock, IAppLinks links, IEmailSender email, ILogger<InvitationService> logger)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public static bool LooksLikeEmail(string value) => value.Contains('@') && !value.Contains(' ') && value.IndexOf('@') > 0 && value.Contains('.');

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Returns the new link, or null when the person already has a pending invitation or an account in the organization.</summary>
    public async Task<InvitationLinkDto?> CreateIfMissingAsync(Guid organizationId, string emailAddress, string displayName, OrganizationRole role,
        Guid? projectId, string actor, bool sendEmail, CancellationToken ct)
    {
        var address = emailAddress.Trim().ToLowerInvariant();
        var now = clock.UtcNow;
        if (await db.Invitations.AnyAsync(i => i.OrganizationId == organizationId && i.Email == address && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now, ct))
            return null;
        if (await db.OrganizationMembers.AnyAsync(m => m.OrganizationId == organizationId && db.Users.Any(u => u.Id == m.UserId && u.Email == address), ct))
            return null;
        var token = NewToken();
        var invitation = new Invitation
        {
            OrganizationId = organizationId, Email = address, DisplayName = displayName.Trim(), Role = role, TokenHash = Hash(token),
            ExpiresAt = now + Lifetime, InvitedBy = actor, ProjectId = projectId,
        };
        db.Invitations.Add(invitation);
        return await DeliverAsync(invitation, token, sendEmail, ct);
    }

    /// <summary>Issues a fresh token for an existing invitation (the old link stops working).</summary>
    public async Task<InvitationLinkDto> RenewAsync(Invitation invitation, bool sendEmail, CancellationToken ct)
    {
        var token = NewToken();
        invitation.TokenHash = Hash(token);
        invitation.ExpiresAt = clock.UtcNow + Lifetime;
        invitation.RevokedAt = null;
        return await DeliverAsync(invitation, token, sendEmail, ct);
    }

    private async Task<InvitationLinkDto> DeliverAsync(Invitation invitation, string token, bool sendEmail, CancellationToken ct)
    {
        var url = links.InvitationUrl(token);
        var sent = false;
        if (sendEmail && email.Enabled)
        {
            try
            {
                var organization = await db.Organizations.AsNoTracking().Where(o => o.Id == invitation.OrganizationId).Select(o => o.Name).FirstAsync(ct);
                await email.SendAsync(invitation.Email, $"Azure Agent: {organization} invitation",
                    $"<p>Hello {WebUtility.HtmlEncode(invitation.DisplayName)},</p><p>You were invited to <b>{WebUtility.HtmlEncode(organization)}</b> on Azure Agent.</p>" +
                    $"<p><a href=\"{url}\">Accept the invitation</a> (valid for {Lifetime.TotalDays:0} days).</p>", ct);
                sent = true;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Sending the invitation e-mail to {Email} failed", invitation.Email);
            }
        }
        return new InvitationLinkDto(invitation.Id, invitation.Email, url, sent);
    }

    /// <summary>Accepts every pending invitation for the address (called once the person is authenticated).</summary>
    public async Task AcceptPendingAsync(User user, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var pending = await db.Invitations.Where(i => i.Email == user.Email && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now).ToListAsync(ct);
        var orgs = (await db.OrganizationMembers.Where(m => m.UserId == user.Id).Select(m => m.OrganizationId).ToListAsync(ct)).ToHashSet();
        foreach (var invitation in pending)
        {
            invitation.AcceptedAt = now;
            invitation.AcceptedByUserId = user.Id;
            if (orgs.Add(invitation.OrganizationId))
                db.OrganizationMembers.Add(new OrganizationMember { OrganizationId = invitation.OrganizationId, UserId = user.Id, Role = invitation.Role });
        }
    }
}

// Admin: create / list / renew / revoke ------------------------------------------------------------

public sealed record InviteePerson(string Email, string DisplayName);

public sealed record CreateInvitationsCommand(IReadOnlyList<InviteePerson> People, OrganizationRole Role, Guid? ProjectId = null);

public sealed class CreateInvitationsCommandValidator : AbstractValidator<CreateInvitationsCommand>
{
    public CreateInvitationsCommandValidator()
    {
        RuleFor(x => x.People).NotEmpty().Must(p => p.Count <= 200).WithMessage("At most 200 invitations at once");
        RuleForEach(x => x.People).ChildRules(p =>
        {
            p.RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
            p.RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(100);
        });
        RuleFor(x => x.Role).IsInEnum();
    }
}

public static class CreateInvitationsHandler
{
    /// <summary>
    /// Organization admins invite anyone; project member managers may invite people of their
    /// project's Azure teams (as organization members).
    /// </summary>
    public static async Task<IReadOnlyList<InvitationLinkDto>> Handle(CreateInvitationsCommand command, IAppDbContext db, AccessControl access,
        ICurrentUser current, InvitationService invitations, CancellationToken ct)
    {
        var organizationId = access.RequireOrganization();
        var isAdmin = current.IsSystemAdmin || await access.OrganizationRoleAsync(ct) is OrganizationRole.Admin or OrganizationRole.Owner;
        if (!isAdmin)
        {
            if (command.ProjectId is not { } projectId) throw new ForbiddenException("Organization admin role required");
            await access.RequireAsync(projectId, Permission.MembersManage, ct);
            if (command.Role != OrganizationRole.Member) throw new ForbiddenException("Only organization admins can invite admins");
            var teamMembers = (await db.Teams.AsNoTracking().Where(t => t.ProjectId == projectId).ToListAsync(ct))
                .SelectMany(t => t.Members).Select(m => m.UniqueName.ToLowerInvariant()).ToHashSet();
            if (command.People.Any(p => !teamMembers.Contains(p.Email.Trim().ToLowerInvariant())))
                throw new ForbiddenException("Project managers can only invite members of the project's Azure teams");
        }
        if (command.Role == OrganizationRole.Owner && await access.OrganizationRoleAsync(ct) != OrganizationRole.Owner && !current.IsSystemAdmin)
            throw new ForbiddenException("Only owners can invite owners");

        var result = new List<InvitationLinkDto>();
        foreach (var person in command.People)
        {
            if (await invitations.CreateIfMissingAsync(organizationId, person.Email, person.DisplayName, command.Role, command.ProjectId, current.ActorName,
                    sendEmail: true, ct) is { } link) result.Add(link);
        }
        db.Audit(current, organizationId, "invitations.created", "organization", organizationId, new { emails = result.Select(r => r.Email), command.Role }, command.ProjectId);
        await db.SaveChangesAsync(ct);
        return result;
    }
}

public sealed record InvitationDto(Guid Id, string Email, string DisplayName, OrganizationRole Role, string Status, DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt, string InvitedBy, Guid? ProjectId);

public sealed record ListInvitationsQuery;

public static class ListInvitationsHandler
{
    public static async Task<IReadOnlyList<InvitationDto>> Handle(ListInvitationsQuery _, IAppDbContext db, AccessControl access, IClock clock, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        var now = clock.UtcNow;
        var rows = await db.Invitations.AsNoTracking().OrderByDescending(i => i.CreatedAt).Take(500).ToListAsync(ct);
        return rows.Select(i => new InvitationDto(i.Id, i.Email, i.DisplayName, i.Role,
            i.AcceptedAt is not null ? "Accepted" : i.RevokedAt is not null ? "Revoked" : i.ExpiresAt <= now ? "Expired" : "Pending",
            i.ExpiresAt, i.CreatedAt, i.InvitedBy, i.ProjectId)).ToList();
    }
}

public sealed record RenewInvitationCommand(Guid InvitationId);

public static class RenewInvitationHandler
{
    public static async Task<InvitationLinkDto> Handle(RenewInvitationCommand command, IAppDbContext db, AccessControl access, ICurrentUser current,
        InvitationService invitations, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.Id == command.InvitationId, ct) ?? throw new NotFoundException("Invitation");
        if (invitation.AcceptedAt is not null) throw new ConflictException("The invitation was already accepted");
        var link = await invitations.RenewAsync(invitation, sendEmail: false, ct);
        db.Audit(current, invitation.OrganizationId, "invitation.renewed", "invitation", invitation.Id, new { invitation.Email });
        await db.SaveChangesAsync(ct);
        return link;
    }
}

public sealed record RevokeInvitationCommand(Guid InvitationId);

public static class RevokeInvitationHandler
{
    public static async Task Handle(RevokeInvitationCommand command, IAppDbContext db, AccessControl access, ICurrentUser current, IClock clock, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.Id == command.InvitationId, ct) ?? throw new NotFoundException("Invitation");
        if (invitation.AcceptedAt is not null) throw new ConflictException("The invitation was already accepted");
        invitation.RevokedAt = clock.UtcNow;
        db.Audit(current, invitation.OrganizationId, "invitation.revoked", "invitation", invitation.Id, new { invitation.Email });
        await db.SaveChangesAsync(ct);
    }
}

// Public: inspect / accept ---------------------------------------------------------------------------

public sealed record InvitationInfoDto(string Email, string DisplayName, string OrganizationName, DateTimeOffset ExpiresAt, bool HasAccount, bool SsoEnabled);

public sealed record GetInvitationQuery(string Token);

public static class GetInvitationHandler
{
    public static async Task<InvitationInfoDto> Handle(GetInvitationQuery query, IAppDbContext db, ITenantContext tenant, IClock clock,
        IExternalIdentityProvider sso, CancellationToken ct)
    {
        tenant.UseAllOrganizations();
        var hash = InvitationService.Hash(query.Token);
        var invitation = await db.Invitations.AsNoTracking().FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invitation is null || !invitation.IsPending(clock.UtcNow)) throw new NotFoundException("Invitation");
        var organization = await db.Organizations.AsNoTracking().Where(o => o.Id == invitation.OrganizationId).Select(o => o.Name).FirstAsync(ct);
        var hasAccount = await db.Users.AnyAsync(u => u.Email == invitation.Email, ct);
        return new InvitationInfoDto(invitation.Email, invitation.DisplayName, organization, invitation.ExpiresAt, hasAccount, sso.Enabled);
    }
}

/// <summary>Accepts an invitation: new people choose a password; existing accounts confirm theirs.</summary>
public sealed record AcceptInvitationCommand(string Token, string? DisplayName, string Password, string? Ip = null);

public sealed class AcceptInvitationCommandValidator : AbstractValidator<AcceptInvitationCommand>
{
    public AcceptInvitationCommandValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(200);
        RuleFor(x => x.DisplayName).MaximumLength(100);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(128);
    }
}

public static class AcceptInvitationHandler
{
    public static async Task<AuthResult> Handle(AcceptInvitationCommand command, IAppDbContext db, ITenantContext tenant, IClock clock,
        IPasswordHasher hasher, ITokenService tokens, InvitationService invitations, MembershipProvisioner provisioner, CancellationToken ct)
    {
        tenant.UseAllOrganizations();
        var hash = InvitationService.Hash(command.Token);
        var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invitation is null || !invitation.IsPending(clock.UtcNow)) throw new NotFoundException("Invitation");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == invitation.Email, ct);
        if (user is null)
        {
            var errors = new PasswordValidator().Validate(command.Password);
            if (!errors.IsValid) throw new ValidationException(errors.Errors);
            user = new User
            {
                Email = invitation.Email,
                DisplayName = string.IsNullOrWhiteSpace(command.DisplayName) ? invitation.DisplayName : command.DisplayName.Trim(),
                PasswordHash = hasher.Hash(command.Password),
            };
            db.Users.Add(user);
        }
        else if (!user.IsActive || user.PasswordHash is null || !hasher.Verify(command.Password, user.PasswordHash))
        {
            throw new UnauthorizedException("Sign in with your existing password to accept the invitation");
        }
        await invitations.AcceptPendingAsync(user, ct);
        db.AuditEntries.Add(new Domain.Auditing.AuditEntry
        {
            OrganizationId = invitation.OrganizationId, ProjectId = invitation.ProjectId, Actor = user.Email, Action = "invitation.accepted",
            EntityType = "invitation", EntityId = invitation.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        await provisioner.ProvisionUserAsync(user, [], ct);
        user.LastLoginAt = clock.UtcNow;
        return await AuthIssuer.IssueAsync(user, db, tokens, clock, command.Ip, ct);
    }
}
