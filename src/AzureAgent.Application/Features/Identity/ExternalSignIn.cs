using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Application.Features.Auth;
using AzureAgent.Domain.Identity;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzureAgent.Application.Features.Identity;

public sealed record AuthProvidersDto(bool Local, bool Entra, string? EntraName);

public sealed record GetAuthProvidersQuery;

public static class GetAuthProvidersHandler
{
    public static AuthProvidersDto Handle(GetAuthProvidersQuery _, IExternalIdentityProvider sso) =>
        new(true, sso.Enabled, sso.Enabled ? sso.Name : null);
}

public sealed record BeginExternalSignInCommand;

public sealed record ExternalSignInStartDto(string AuthorizeUrl);

public static class BeginExternalSignInHandler
{
    public static async Task<ExternalSignInStartDto> Handle(BeginExternalSignInCommand _, IExternalIdentityProvider sso, CancellationToken ct)
    {
        if (!sso.Enabled) throw new BusinessRuleException("Entra ID sign-in is not configured");
        return new ExternalSignInStartDto(await sso.BeginAsync(ct));
    }
}

public sealed record CompleteExternalSignInCommand(string Code, string State, string? Ip = null);

public sealed class CompleteExternalSignInCommandValidator : AbstractValidator<CompleteExternalSignInCommand>
{
    public CompleteExternalSignInCommandValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(4000);
        RuleFor(x => x.State).NotEmpty().MaximumLength(200);
    }
}

/// <summary>
/// Signs a person in with Entra ID. Known identities sign in directly; a new identity gets an
/// account only when it is invited, is a member of a synced Azure team, or is in a mapped group.
/// The delegated Azure DevOps token from the sign-in becomes the user's personal credential.
/// </summary>
public static class CompleteExternalSignInHandler
{
    public static async Task<AuthResult> Handle(CompleteExternalSignInCommand command, IAppDbContext db, ITenantContext tenant, IClock clock,
        ITokenService tokens, IExternalIdentityProvider sso, InvitationService invitations, MembershipProvisioner provisioner,
        UserAzureCredentials credentials, ILogger<CompleteExternalSignInCommand> logger, CancellationToken ct)
    {
        if (!sso.Enabled) throw new BusinessRuleException("Entra ID sign-in is not configured");
        var identity = await sso.CompleteAsync(command.Code, command.State, ct);
        var email = identity.Email.Trim().ToLowerInvariant();
        if (!InvitationService.LooksLikeEmail(email)) throw new UnauthorizedException("Your Microsoft account has no e-mail address");
        tenant.UseAllOrganizations();

        var login = await db.ExternalLogins.FirstOrDefaultAsync(l => l.Provider == identity.Provider && l.Subject == identity.Subject, ct);
        var user = login is null
            ? await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct)
            : await db.Users.FirstOrDefaultAsync(u => u.Id == login.UserId, ct);
        if (user is { IsActive: false }) throw new UnauthorizedException("Your account is disabled");

        if (user is null)
        {
            var now = clock.UtcNow;
            var invited = await db.Invitations.AnyAsync(i => i.Email == email && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now, ct);
            var inTeam = (await db.Teams.AsNoTracking().ToListAsync(ct)).Any(t => t.Members.Any(m => m.UniqueName.Equals(email, StringComparison.OrdinalIgnoreCase)));
            var inGroup = identity.Groups.Count > 0 && (await db.Projects.AsNoTracking().ToListAsync(ct))
                .Any(p => MembershipProvisioner.GroupRole(p.Settings, identity.Groups) is not null);
            if (!invited && !inTeam && !inGroup)
            {
                logger.LogInformation("Rejected Entra ID sign-in of {Email}: not invited, not in an Azure team or mapped group", email);
                throw new UnauthorizedException("You do not have access yet. Ask an administrator to invite you or add you to the Azure DevOps team.");
            }
            user = new User { Email = email, DisplayName = identity.DisplayName.Length > 0 ? identity.DisplayName : email };
            db.Users.Add(user);
            logger.LogInformation("Created account for {Email} from Entra ID sign-in", email);
        }
        if (login is null)
        {
            db.ExternalLogins.Add(new ExternalLogin { UserId = user.Id, Provider = identity.Provider, Subject = identity.Subject, Email = email, LastLoginAt = clock.UtcNow });
        }
        else
        {
            login.Email = email;
            login.LastLoginAt = clock.UtcNow;
        }
        await invitations.AcceptPendingAsync(user, ct);
        if (identity.AzureDevOpsToken is { } token) await credentials.StoreOAuthAsync(user.Id, email, identity.DisplayName, token, ct);
        await db.SaveChangesAsync(ct);

        var hasAccess = await provisioner.ProvisionUserAsync(user, identity.Groups, ct);
        if (!hasAccess && !user.IsSystemAdmin)
            throw new UnauthorizedException("You do not have access to any organization yet. Ask an administrator to invite you.");
        user.LastLoginAt = clock.UtcNow;
        user.FailedLoginCount = 0;
        return await AuthIssuer.IssueAsync(user, db, tokens, clock, command.Ip, ct);
    }
}
