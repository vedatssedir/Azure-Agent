using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Domain.Identity;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Features.Auth;

public sealed record AuthResult(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, UserDto User);

public sealed record UserDto(Guid Id, string Email, string DisplayName, bool IsSystemAdmin);

public sealed record OrganizationMembershipDto(Guid OrganizationId, string Name, string Slug, OrganizationRole Role);

public sealed record MeDto(UserDto User, IReadOnlyList<OrganizationMembershipDto> Organizations);

internal static class AuthIssuer
{
    public static async Task<AuthResult> IssueAsync(User user, IAppDbContext db, ITokenService tokens, IClock clock, string? ip, CancellationToken ct)
    {
        var access = tokens.CreateAccessToken(user);
        var (refresh, hash) = tokens.CreateRefreshToken();
        db.RefreshTokens.Add(new RefreshToken { UserId = user.Id, TokenHash = hash, ExpiresAt = clock.UtcNow + tokens.RefreshTokenLifetime, CreatedByIp = ip });
        await db.SaveChangesAsync(ct);
        return new AuthResult(access.Token, access.ExpiresAt, refresh, new UserDto(user.Id, user.Email, user.DisplayName, user.IsSystemAdmin));
    }
}

// Setup -----------------------------------------------------------------------------

/// <summary>First-run bootstrap: creates the system administrator and the first organization.</summary>
public sealed record SetupCommand(string Email, string DisplayName, string Password, string OrganizationName, string? Ip = null);

public sealed class SetupCommandValidator : AbstractValidator<SetupCommand>
{
    public SetupCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Password).SetValidator(new PasswordValidator());
        RuleFor(x => x.OrganizationName).NotEmpty().MaximumLength(100);
    }
}

public sealed class PasswordValidator : AbstractValidator<string>
{
    public PasswordValidator()
    {
        RuleFor(x => x).NotEmpty().WithName("Password").MinimumLength(10).MaximumLength(128)
            .Matches("[A-Z]").WithMessage("Password must contain an uppercase letter")
            .Matches("[a-z]").WithMessage("Password must contain a lowercase letter")
            .Matches("[0-9]").WithMessage("Password must contain a digit");
    }
}

public static class SetupHandler
{
    public static async Task<AuthResult> Handle(SetupCommand command, IAppDbContext db, IPasswordHasher hasher, ITokenService tokens,
        IClock clock, ITenantContext tenant, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(ct)) throw new ConflictException("Setup has already been completed");
        var user = new User
        {
            Email = command.Email.Trim().ToLowerInvariant(),
            DisplayName = command.DisplayName.Trim(),
            PasswordHash = hasher.Hash(command.Password),
            IsSystemAdmin = true,
        };
        var organization = new Organization { Name = command.OrganizationName.Trim(), Slug = Slugs.From(command.OrganizationName) };
        tenant.Use(organization.Id);
        db.Users.Add(user);
        db.Organizations.Add(organization);
        db.OrganizationMembers.Add(new OrganizationMember { OrganizationId = organization.Id, UserId = user.Id, Role = OrganizationRole.Owner });
        db.AuditEntries.Add(new Domain.Auditing.AuditEntry
        {
            OrganizationId = organization.Id, Actor = user.Email, Action = "setup.completed", EntityType = "organization", EntityId = organization.Id.ToString(),
        });
        return await AuthIssuer.IssueAsync(user, db, tokens, clock, command.Ip, ct);
    }
}

// Login -----------------------------------------------------------------------------

public sealed record LoginCommand(string Email, string Password, string? Ip = null);

public sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public static class LoginHandler
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public static async Task<AuthResult> Handle(LoginCommand command, IAppDbContext db, IPasswordHasher hasher, ITokenService tokens,
        IClock clock, CancellationToken ct)
    {
        var email = command.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        // Same message for unknown user, wrong password and inactive account.
        var invalid = new UnauthorizedException("Invalid email or password");
        if (user is null || !user.IsActive) throw invalid;
        if (user.LockedUntil > clock.UtcNow) throw new UnauthorizedException("Account is temporarily locked. Try again later.");
        if (!hasher.Verify(command.Password, user.PasswordHash))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailedAttempts)
            {
                user.LockedUntil = clock.UtcNow + LockoutDuration;
                user.FailedLoginCount = 0;
            }
            await db.SaveChangesAsync(ct);
            throw invalid;
        }
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.LastLoginAt = clock.UtcNow;
        return await AuthIssuer.IssueAsync(user, db, tokens, clock, command.Ip, ct);
    }
}

// Refresh / logout --------------------------------------------------------------------

public sealed record RefreshCommand(string RefreshToken, string? Ip = null);

public sealed class RefreshCommandValidator : AbstractValidator<RefreshCommand>
{
    public RefreshCommandValidator() => RuleFor(x => x.RefreshToken).NotEmpty();
}

public static class RefreshHandler
{
    /// <summary>Rotates the refresh token. Reusing a rotated token revokes the whole chain (theft detection).</summary>
    public static async Task<AuthResult> Handle(RefreshCommand command, IAppDbContext db, ITokenService tokens, IClock clock, CancellationToken ct)
    {
        var hash = tokens.HashRefreshToken(command.RefreshToken);
        var existing = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct) ?? throw new UnauthorizedException("Invalid refresh token");
        if (existing.RevokedAt is not null)
        {
            var active = await db.RefreshTokens.Where(t => t.UserId == existing.UserId && t.RevokedAt == null).ToListAsync(ct);
            foreach (var token in active) token.RevokedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedException("Refresh token reuse detected; all sessions were signed out");
        }
        if (!existing.IsActive(clock.UtcNow)) throw new UnauthorizedException("Refresh token expired");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == existing.UserId && u.IsActive, ct) ?? throw new UnauthorizedException("Invalid refresh token");
        var result = await AuthIssuer.IssueAsync(user, db, tokens, clock, command.Ip, ct);
        var replacement = await db.RefreshTokens.FirstAsync(t => t.TokenHash == tokens.HashRefreshToken(result.RefreshToken), ct);
        existing.RevokedAt = clock.UtcNow;
        existing.ReplacedById = replacement.Id;
        await db.SaveChangesAsync(ct);
        return result;
    }
}

public sealed record LogoutCommand(string RefreshToken);

public static class LogoutHandler
{
    public static async Task Handle(LogoutCommand command, IAppDbContext db, ITokenService tokens, IClock clock, CancellationToken ct)
    {
        var hash = tokens.HashRefreshToken(command.RefreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is { RevokedAt: null })
        {
            token.RevokedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }
}

// Me ----------------------------------------------------------------------------------

public sealed record GetMeQuery;

public static class GetMeHandler
{
    public static async Task<MeDto> Handle(GetMeQuery _, IAppDbContext db, ICurrentUser current, ITenantContext tenant, CancellationToken ct)
    {
        var userId = current.UserId ?? throw new UnauthorizedException();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new UnauthorizedException();
        tenant.UseAllOrganizations();
        var organizations = await db.OrganizationMembers.AsNoTracking().Where(m => m.UserId == userId)
            .Join(db.Organizations, m => m.OrganizationId, o => o.Id, (m, o) => new { m, o })
            .OrderBy(x => x.o.Name)
            .Select(x => new OrganizationMembershipDto(x.o.Id, x.o.Name, x.o.Slug, x.m.Role)).ToListAsync(ct);
        return new MeDto(new UserDto(user.Id, user.Email, user.DisplayName, user.IsSystemAdmin), organizations);
    }
}

// Change password -----------------------------------------------------------------------

public sealed record ChangePasswordCommand(string CurrentPassword, string NewPassword);

public sealed class ChangePasswordCommandValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordCommandValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
        RuleFor(x => x.NewPassword).SetValidator(new PasswordValidator()).NotEqual(x => x.CurrentPassword);
    }
}

public static class ChangePasswordHandler
{
    public static async Task Handle(ChangePasswordCommand command, IAppDbContext db, ICurrentUser current, IPasswordHasher hasher, IClock clock, CancellationToken ct)
    {
        var userId = current.UserId ?? throw new UnauthorizedException();
        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        if (!hasher.Verify(command.CurrentPassword, user.PasswordHash)) throw new BusinessRuleException("Current password is incorrect");
        user.PasswordHash = hasher.Hash(command.NewPassword);
        foreach (var token in await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct)) token.RevokedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
