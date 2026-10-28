using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Application.Features.Auth;
using AzureAgent.Domain.Identity;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzureAgent.Application.Features.Identity;

/// <summary>
/// A developer of the connected Azure DevOps organization creates their own account. They prove
/// who they are with their own Azure PAT: Azure must report the same e-mail for it. The PAT then
/// becomes their personal Azure credential, and their team memberships give them their projects.
/// </summary>
public sealed record SignUpCommand(string Email, string DisplayName, string Password, string PersonalAccessToken, string? Ip = null);

public sealed class SignUpCommandValidator : AbstractValidator<SignUpCommand>
{
    public SignUpCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Password).SetValidator(new PasswordValidator());
        RuleFor(x => x.PersonalAccessToken).NotEmpty().Length(20, 500).Matches("^[A-Za-z0-9]+$").WithMessage("A PAT contains letters and digits only");
    }
}

public static class SignUpHandler
{
    public static async Task<AuthResult> Handle(SignUpCommand command, IAppDbContext db, ITenantContext tenant, IPasswordHasher hasher, ITokenService tokens, IClock clock,
        IAzureDevOpsClientFactory factory, ISecretProtector protector, InvitationService invitations, MembershipProvisioner provisioner,
        ILogger<SignUpCommand> logger, CancellationToken ct)
    {
        tenant.UseAllOrganizations();
        var email = command.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct)) throw new ConflictException("An account with this e-mail already exists; sign in instead");

        // Organizations that allow sign-up and have this person in one of their Azure teams (or invited them).
        var organizations = (await db.Organizations.AsNoTracking().Where(o => o.AzureOrganizationUrl != null).ToListAsync(ct)).Where(o => o.Settings.AllowSelfSignUp).ToList();
        var teamOrgs = (await db.Teams.AsNoTracking().ToListAsync(ct))
            .Where(t => t.Members.Any(m => m.UniqueName.Equals(email, StringComparison.OrdinalIgnoreCase))).Select(t => t.OrganizationId).ToHashSet();
        var now = clock.UtcNow;
        var invitedOrgs = (await db.Invitations.AsNoTracking().Where(i => i.Email == email && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .Select(i => i.OrganizationId).ToListAsync(ct)).ToHashSet();
        var organization = organizations.FirstOrDefault(o => teamOrgs.Contains(o.Id) || invitedOrgs.Contains(o.Id))
            ?? throw new UnauthorizedException("This e-mail is not a member of any Azure DevOps team of the connected organization. Ask your administrator.");

        AzureIdentity identity;
        try
        {
            identity = await factory.Create(new AzureConnectionInfo(organization.AzureOrganizationUrl!, "", AzureCredential.Pat(command.PersonalAccessToken.Trim())))
                .GetAuthenticatedIdentityAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogInformation("Sign-up PAT check failed for {Email}: {Message}", email, e.Message);
            throw new BusinessRuleException($"Azure DevOps rejected the PAT for {organization.AzureOrganizationUrl}");
        }
        if (!identity.UniqueName.Equals(email, StringComparison.OrdinalIgnoreCase))
            throw new BusinessRuleException($"This PAT belongs to {identity.UniqueName}, not to {email}");

        var user = new User { Email = email, DisplayName = command.DisplayName.Trim(), PasswordHash = hasher.Hash(command.Password), LastLoginAt = now };
        db.Users.Add(user);
        db.UserAzureCredentials.Add(new UserAzureCredential
        {
            UserId = user.Id, Kind = AzureCredentialKind.PersonalAccessToken, AzureOrganizationUrl = UserAzureCredentials.NormalizeUrl(organization.AzureOrganizationUrl!),
            Ciphertext = protector.Protect(command.PersonalAccessToken.Trim()), AzureUniqueName = email, AzureDisplayName = identity.DisplayName, VerifiedAt = now,
        });
        await invitations.AcceptPendingAsync(user, ct);
        db.AuditEntries.Add(new Domain.Auditing.AuditEntry
        {
            OrganizationId = organization.Id, Actor = email, Action = "user.signed_up", EntityType = "user", EntityId = user.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        await provisioner.ProvisionUserAsync(user, [], ct);
        logger.LogInformation("{Email} signed up with a verified Azure PAT", email);
        return await AuthIssuer.IssueAsync(user, db, tokens, clock, command.Ip, ct);
    }
}
