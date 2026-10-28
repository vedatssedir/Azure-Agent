using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Application.Features.Projects;
using AzureAgent.Domain.Identity;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzureAgent.Application.Features.Identity;

/// <summary>Resolves (and refreshes) a user's own Azure DevOps credential.</summary>
public sealed class UserAzureCredentials(IAppDbContext db, ISecretProtector protector, IAzureTokenProvider tokens, IClock clock,
    ILogger<UserAzureCredentials> logger)
{
    public static string NormalizeUrl(string url) => url.Trim().TrimEnd('/').ToLowerInvariant();

    public async Task<AzureCredential?> ResolveAsync(Guid userId, string azureOrganizationUrl, CancellationToken ct)
    {
        var url = NormalizeUrl(azureOrganizationUrl);
        var candidates = await db.UserAzureCredentials.Where(c => c.UserId == userId && (c.AzureOrganizationUrl == url || c.AzureOrganizationUrl == null))
            .ToListAsync(ct);
        // An organization-specific PAT wins over the tenant-wide OAuth token.
        foreach (var credential in candidates.OrderBy(c => c.AzureOrganizationUrl == null))
        {
            if (credential.Kind == AzureCredentialKind.PersonalAccessToken)
            {
                if (credential.ExpiresAt is { } expires && expires <= clock.UtcNow) continue;
                return AzureCredential.Pat(protector.Unprotect(credential.Ciphertext), credential.AzureUniqueName);
            }
            if (credential.ExpiresAt > clock.UtcNow.AddMinutes(2))
                return AzureCredential.Bearer(protector.Unprotect(credential.Ciphertext), credential.AzureUniqueName);
            if (credential.RefreshCiphertext is null) continue;
            try
            {
                var refreshed = await tokens.RefreshUserTokenAsync(protector.Unprotect(credential.RefreshCiphertext), ct);
                credential.Ciphertext = protector.Protect(refreshed.AccessToken);
                if (refreshed.RefreshToken is { Length: > 0 } next) credential.RefreshCiphertext = protector.Protect(next);
                credential.ExpiresAt = refreshed.ExpiresAt;
                credential.LastError = null;
                await db.SaveChangesAsync(ct);
                return AzureCredential.Bearer(refreshed.AccessToken, credential.AzureUniqueName);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Refreshing the Azure DevOps token of user {UserId} failed", userId);
                credential.LastError = "Token refresh failed; sign in with Microsoft again";
                await db.SaveChangesAsync(ct);
            }
        }
        return null;
    }

    /// <summary>Stores the delegated token from an Entra ID sign-in (one per user, all organizations).</summary>
    public async Task StoreOAuthAsync(Guid userId, string uniqueName, string displayName, OAuthToken token, CancellationToken ct)
    {
        var existing = await db.UserAzureCredentials.FirstOrDefaultAsync(c => c.UserId == userId && c.Kind == AzureCredentialKind.OAuth, ct);
        if (existing is null)
        {
            existing = new UserAzureCredential
            {
                UserId = userId, Kind = AzureCredentialKind.OAuth, Ciphertext = [], AzureUniqueName = uniqueName,
            };
            db.UserAzureCredentials.Add(existing);
        }
        existing.Ciphertext = protector.Protect(token.AccessToken);
        if (token.RefreshToken is { Length: > 0 } refresh) existing.RefreshCiphertext = protector.Protect(refresh);
        existing.ExpiresAt = token.ExpiresAt;
        existing.AzureUniqueName = uniqueName;
        existing.AzureDisplayName = displayName;
        existing.VerifiedAt = clock.UtcNow;
        existing.LastError = null;
    }
}

public sealed record AzureCredentialDto(Guid Id, AzureCredentialKind Kind, string? AzureOrganizationUrl, string AzureUniqueName, string? AzureDisplayName,
    DateTimeOffset VerifiedAt, DateTimeOffset? ExpiresAt, string? LastError);

public sealed record ListMyAzureCredentialsQuery;

public static class ListMyAzureCredentialsHandler
{
    public static async Task<IReadOnlyList<AzureCredentialDto>> Handle(ListMyAzureCredentialsQuery _, IAppDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var userId = user.UserId ?? throw new UnauthorizedException();
        return await db.UserAzureCredentials.AsNoTracking().Where(c => c.UserId == userId).OrderBy(c => c.CreatedAt)
            .Select(c => new AzureCredentialDto(c.Id, c.Kind, c.AzureOrganizationUrl, c.AzureUniqueName, c.AzureDisplayName, c.VerifiedAt, c.ExpiresAt, c.LastError))
            .ToListAsync(ct);
    }
}

/// <summary>Connects the caller's own PAT. Azure must report the caller's identity for it, so nobody can act through someone else's token.</summary>
public sealed record SaveMyAzurePatCommand(string AzureOrganizationUrl, string PersonalAccessToken, DateTimeOffset? ExpiresAt);

public sealed class SaveMyAzurePatCommandValidator : AbstractValidator<SaveMyAzurePatCommand>
{
    public SaveMyAzurePatCommandValidator()
    {
        RuleFor(x => x.AzureOrganizationUrl).SetValidator(new AzureUrlValidator());
        RuleFor(x => x.PersonalAccessToken).NotEmpty().Length(20, 500).Matches("^[A-Za-z0-9]+$").WithMessage("A PAT contains letters and digits only");
    }
}

public static class SaveMyAzurePatHandler
{
    public static async Task<AzureCredentialDto> Handle(SaveMyAzurePatCommand command, IAppDbContext db, ICurrentUser current, ITenantContext tenant,
        IAzureDevOpsClientFactory factory, ISecretProtector protector, IClock clock, ILogger<SaveMyAzurePatCommand> logger, CancellationToken ct)
    {
        var userId = current.UserId ?? throw new UnauthorizedException();
        var url = UserAzureCredentials.NormalizeUrl(command.AzureOrganizationUrl);
        AzureIdentity identity;
        try
        {
            identity = await factory.Create(new AzureConnectionInfo(url, "", AzureCredential.Pat(command.PersonalAccessToken)))
                .GetAuthenticatedIdentityAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogInformation("PAT verification failed for user {UserId}: {Message}", userId, e.Message);
            throw new BusinessRuleException("Azure DevOps rejected the PAT for this organization");
        }
        var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
        tenant.UseAllOrganizations();
        var known = await db.ProjectMembers.AsNoTracking().Where(m => m.UserId == userId && m.AzureUniqueName != null)
            .Select(m => m.AzureUniqueName!).ToListAsync(ct);
        var reported = identity.UniqueName.Trim().ToLowerInvariant();
        if (reported.Length == 0 || !known.Append(user.Email).Contains(reported))
            throw new BusinessRuleException($"This PAT belongs to {identity.UniqueName}, not to your account ({user.Email})");

        var existing = await db.UserAzureCredentials.FirstOrDefaultAsync(c => c.UserId == userId && c.Kind == AzureCredentialKind.PersonalAccessToken
            && c.AzureOrganizationUrl == url, ct);
        if (existing is null)
        {
            existing = new UserAzureCredential
            {
                UserId = userId, Kind = AzureCredentialKind.PersonalAccessToken, AzureOrganizationUrl = url, Ciphertext = [], AzureUniqueName = reported,
            };
            db.UserAzureCredentials.Add(existing);
        }
        existing.Ciphertext = protector.Protect(command.PersonalAccessToken);
        existing.ExpiresAt = command.ExpiresAt;
        existing.AzureUniqueName = reported;
        existing.AzureDisplayName = identity.DisplayName;
        existing.VerifiedAt = clock.UtcNow;
        existing.LastError = null;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("User {UserId} connected an Azure DevOps PAT for {Organization}", userId, url);
        return new AzureCredentialDto(existing.Id, existing.Kind, existing.AzureOrganizationUrl, existing.AzureUniqueName, existing.AzureDisplayName,
            existing.VerifiedAt, existing.ExpiresAt, null);
    }
}

public sealed record DeleteMyAzureCredentialCommand(Guid Id);

public static class DeleteMyAzureCredentialHandler
{
    public static async Task Handle(DeleteMyAzureCredentialCommand command, IAppDbContext db, ICurrentUser current, CancellationToken ct)
    {
        var userId = current.UserId ?? throw new UnauthorizedException();
        var credential = await db.UserAzureCredentials.FirstOrDefaultAsync(c => c.Id == command.Id && c.UserId == userId, ct)
            ?? throw new NotFoundException("Azure credential");
        db.UserAzureCredentials.Remove(credential);
        await db.SaveChangesAsync(ct);
    }
}
