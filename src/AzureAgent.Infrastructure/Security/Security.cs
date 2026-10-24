using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AzureAgent.Application.Abstractions;
using AzureAgent.Domain.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AzureAgent.Infrastructure.Security;

public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "azure-agent";
    public string Audience { get; set; } = "azure-agent";
    /// <summary>HMAC-SHA256 signing key, at least 32 bytes.</summary>
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 14;
}

public sealed class EncryptionOptions
{
    public const string Section = "Encryption";
    /// <summary>Base64 AES-256 key for secrets at rest.</summary>
    public string Key { get; set; } = "";
}

public static class Claims
{
    public const string SystemAdmin = "sys_admin";
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class BcryptPasswordHasher : IPasswordHasher
{
    public string Hash(string password) => BCrypt.Net.BCrypt.EnhancedHashPassword(password, workFactor: 12);
    public bool Verify(string password, string hash) => BCrypt.Net.BCrypt.EnhancedVerify(password, hash);
}

public sealed class JwtTokenService(IOptions<JwtOptions> options, IClock clock) : ITokenService
{
    private readonly JwtOptions _options = options.Value;

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(_options.RefreshTokenDays);

    public AccessToken CreateAccessToken(User user)
    {
        var expires = clock.UtcNow.AddMinutes(_options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Name, user.DisplayName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        if (user.IsSystemAdmin) claims.Add(new Claim(Claims.SystemAdmin, "true"));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var token = new JwtSecurityToken(_options.Issuer, _options.Audience, claims, clock.UtcNow.UtcDateTime, expires.UtcDateTime,
            new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new AccessToken(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public (string Token, string Hash) CreateRefreshToken()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (token, HashRefreshToken(token));
    }

    public string HashRefreshToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>AES-256-GCM. Layout: 12-byte nonce | 16-byte tag | ciphertext.</summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private readonly byte[] _key;

    public AesGcmSecretProtector(IOptions<EncryptionOptions> options)
    {
        _key = Convert.FromBase64String(options.Value.Key);
        if (_key.Length != 32) throw new InvalidOperationException("Encryption:Key must be a base64 encoded 32-byte key");
    }

    public byte[] Protect(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var result = new byte[12 + 16 + data.Length];
        var nonce = result.AsSpan(0, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, data, result.AsSpan(28), result.AsSpan(12, 16));
        return result;
    }

    public string Unprotect(byte[] ciphertext)
    {
        var plaintext = new byte[ciphertext.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(ciphertext.AsSpan(0, 12), ciphertext.AsSpan(28), ciphertext.AsSpan(12, 16), plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? UserId => Guid.TryParse(Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
    public string? Email => Principal?.FindFirstValue(JwtRegisteredClaimNames.Email) ?? Principal?.FindFirstValue(ClaimTypes.Email);
    public bool IsSystemAdmin => Principal?.HasClaim(Claims.SystemAdmin, "true") == true;
}

/// <summary>
/// Tenant for the current unit of work. Explicit <see cref="Use"/> wins; otherwise the organization
/// verified by <c>TenantResolutionMiddleware</c> for the current HTTP request is used. Wolverine
/// resolves handler dependencies in its own scope, so the request value travels on HttpContext.
/// </summary>
public sealed class TenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    public const string HttpItemKey = "azure-agent.organization";
    private Guid? _explicit;

    public Guid? OrganizationId => _explicit ?? (accessor.HttpContext?.Items[HttpItemKey] as Guid?);
    public bool BypassFilter { get; private set; }

    public void Use(Guid organizationId)
    {
        _explicit = organizationId;
        BypassFilter = false;
    }

    public void UseAllOrganizations() => BypassFilter = true;
}
