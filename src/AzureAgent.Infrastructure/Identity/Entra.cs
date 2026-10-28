using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AzureAgent.Infrastructure.Identity;

public sealed class EntraOptions
{
    public const string Section = "Entra";
    public bool Enabled { get; set; }
    public string DisplayName { get; set; } = "Microsoft";
    public string Instance { get; set; } = "https://login.microsoftonline.com/";
    /// <summary>A tenant id, or "organizations" for multi-tenant sign-in (then <see cref="AllowedTenantIds"/> is required).</summary>
    public string TenantId { get; set; } = "organizations";
    public List<string> AllowedTenantIds { get; set; } = [];
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>The web app's callback page, e.g. https://agent.contoso.com/auth/callback (registered in the app registration).</summary>
    public string RedirectUri { get; set; } = "";
    /// <summary>Also request delegated Azure DevOps access, so actions run as the signed-in person.</summary>
    public bool RequestAzureDevOpsAccess { get; set; } = true;

    public bool IsMultiTenant => TenantId is "organizations" or "common";

    public string Authority => $"{Instance.TrimEnd('/')}/{TenantId}";

    public bool TenantAllowed(string tenantId) => IsMultiTenant
        ? AllowedTenantIds.Contains(tenantId, StringComparer.OrdinalIgnoreCase)
        : string.Equals(tenantId, TenantId, StringComparison.OrdinalIgnoreCase);

    public IEnumerable<string> Problems()
    {
        if (!Enabled) yield break;
        if (ClientId.Length == 0) yield return "Entra:ClientId is required";
        if (ClientSecret.Length == 0) yield return "Entra:ClientSecret is required";
        if (!Uri.TryCreate(RedirectUri, UriKind.Absolute, out _)) yield return "Entra:RedirectUri must be an absolute URL";
        if (IsMultiTenant && AllowedTenantIds.Count == 0) yield return "Entra:AllowedTenantIds is required for multi-tenant sign-in";
    }
}

internal sealed class TokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
    [JsonPropertyName("id_token")] public string? IdToken { get; set; }
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
}

public static class AzureDevOpsResource
{
    /// <summary>Well-known application id of Azure DevOps.</summary>
    public const string Id = "499b84ac-1321-427f-aa17-267ca6975798";
    public const string DefaultScope = Id + "/.default";
    public const string UserImpersonation = Id + "/user_impersonation";
}

/// <summary>Entra ID token endpoint client (service principal and refresh-token grants).</summary>
public sealed class EntraTokenProvider(IHttpClientFactory http, IOptions<EntraOptions> options, IMemoryCache cache, IClock clock) : IAzureTokenProvider
{
    public const string HttpClientName = "entra";

    public async Task<OAuthToken> GetServicePrincipalTokenAsync(string tenantId, string clientId, string clientSecret, CancellationToken ct)
    {
        var key = $"sp-token:{tenantId}:{clientId}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientSecret)))[..16]}";
        if (cache.TryGetValue(key, out OAuthToken? cached) && cached!.ExpiresAt > clock.UtcNow.AddMinutes(5)) return cached;
        var token = await RequestAsync($"{options.Value.Instance.TrimEnd('/')}/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/token", new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = AzureDevOpsResource.DefaultScope,
        }, ct);
        var result = new OAuthToken(token.AccessToken!, null, clock.UtcNow.AddSeconds(token.ExpiresIn));
        cache.Set(key, result, result.ExpiresAt - clock.UtcNow);
        return result;
    }

    public async Task<OAuthToken> RefreshUserTokenAsync(string refreshToken, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled) throw new BusinessRuleException("Entra ID sign-in is not configured");
        var token = await RequestAsync($"{o.Authority}/oauth2/v2.0/token", new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = o.ClientId,
            ["client_secret"] = o.ClientSecret,
            ["refresh_token"] = refreshToken,
            ["scope"] = $"{AzureDevOpsResource.UserImpersonation} offline_access",
        }, ct);
        return new OAuthToken(token.AccessToken!, token.RefreshToken, clock.UtcNow.AddSeconds(token.ExpiresIn));
    }

    internal async Task<TokenResponse> RequestAsync(string endpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await http.CreateClient(HttpClientName).PostAsync(endpoint, new FormUrlEncodedContent(form), ct);
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(ct) ?? new TokenResponse();
        // error_description can be long and carries correlation ids only; never the secret.
        if (!response.IsSuccessStatusCode || token.AccessToken is null)
            throw new UnauthorizedException($"Entra ID token request failed: {token.Error ?? ((int)response.StatusCode).ToString()}");
        return token;
    }
}

/// <summary>
/// OpenID Connect authorization code flow with PKCE against Entra ID. State, nonce and verifier
/// live in the distributed cache for ten minutes and are single-use; the ID token is validated
/// against the tenant's signing keys, audience, issuer (per tenant), lifetime and nonce.
/// </summary>
public sealed class EntraIdentityProvider(IOptions<EntraOptions> options, ICacheService cache, EntraTokenProvider tokens, IClock clock,
    ILogger<EntraIdentityProvider> logger) : IExternalIdentityProvider
{
    private static readonly SemaphoreSlim ConfigurationLock = new(1, 1);
    private static ConfigurationManager<OpenIdConnectConfiguration>? _configuration;

    private sealed record PendingSignIn(string Nonce, string Verifier);

    public bool Enabled => options.Value.Enabled;
    public string Name => options.Value.DisplayName;

    private static string Random() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public async Task<string> BeginAsync(CancellationToken ct)
    {
        var o = options.Value;
        var state = Random();
        var pending = new PendingSignIn(Random(), Random());
        await cache.SetAsync($"oidc:{state}", pending, TimeSpan.FromMinutes(10), ct);
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(pending.Verifier)));
        var query = new Dictionary<string, string>
        {
            ["client_id"] = o.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = o.RedirectUri,
            ["response_mode"] = "query",
            ["scope"] = Scopes(o),
            ["state"] = state,
            ["nonce"] = pending.Nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "select_account",
        };
        return $"{o.Authority}/oauth2/v2.0/authorize?{string.Join('&', query.Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value)}"))}";
    }

    private static string Scopes(EntraOptions o) =>
        o.RequestAzureDevOpsAccess ? $"openid profile email offline_access {AzureDevOpsResource.UserImpersonation}" : "openid profile email";

    public async Task<ExternalIdentity> CompleteAsync(string code, string state, CancellationToken ct)
    {
        var o = options.Value;
        var pending = await cache.GetAsync<PendingSignIn>($"oidc:{state}", ct) ?? throw new UnauthorizedException("The sign-in expired; start again");
        await cache.RemoveAsync($"oidc:{state}", ct);
        var token = await tokens.RequestAsync($"{o.Authority}/oauth2/v2.0/token", new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = o.ClientId,
            ["client_secret"] = o.ClientSecret,
            ["code"] = code,
            ["redirect_uri"] = o.RedirectUri,
            ["code_verifier"] = pending.Verifier,
            ["scope"] = Scopes(o),
        }, ct);
        if (token.IdToken is null) throw new UnauthorizedException("Entra ID returned no ID token");
        var configuration = await ConfigurationAsync(o, ct);
        var identity = await ValidateIdTokenAsync(token.IdToken, o, configuration.SigningKeys, pending.Nonce);
        var azureDevOps = o.RequestAzureDevOpsAccess && token.AccessToken is not null
            ? new OAuthToken(token.AccessToken, token.RefreshToken, clock.UtcNow.AddSeconds(token.ExpiresIn))
            : null;
        return identity with { AzureDevOpsToken = azureDevOps };
    }

    /// <summary>Validates an Entra ID v2 ID token and maps its claims. Public for unit tests.</summary>
    public static async Task<ExternalIdentity> ValidateIdTokenAsync(string idToken, EntraOptions o, IEnumerable<SecurityKey> keys, string nonce)
    {
        var parameters = new TokenValidationParameters
        {
            ValidAudience = o.ClientId,
            IssuerSigningKeys = keys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            IssuerValidator = (issuer, securityToken, _) =>
            {
                var tenantId = (securityToken as JsonWebToken)?.GetPayloadValue<string>("tid") ?? "";
                if (!o.TenantAllowed(tenantId)) throw new SecurityTokenInvalidIssuerException($"Tenant {tenantId} is not allowed");
                if (!string.Equals(issuer, $"{o.Instance.TrimEnd('/')}/{tenantId}/v2.0", StringComparison.OrdinalIgnoreCase))
                    throw new SecurityTokenInvalidIssuerException("Unexpected issuer");
                return issuer;
            },
        };
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, parameters);
        if (!result.IsValid) throw new UnauthorizedException($"Invalid ID token: {result.Exception?.GetType().Name}");
        var jwt = (JsonWebToken)result.SecurityToken;
        if (!jwt.TryGetPayloadValue<string>("nonce", out var tokenNonce) || tokenNonce != nonce) throw new UnauthorizedException("Invalid ID token nonce");
        string Claim(string name) => jwt.TryGetPayloadValue<string>(name, out var value) ? value : "";
        var groups = jwt.Claims.Where(c => c.Type == "groups").Select(c => c.Value).ToList();
        var email = Claim("email") is { Length: > 0 } e ? e : Claim("preferred_username");
        return new ExternalIdentity("entra", $"{Claim("tid")}:{Claim("oid")}", email, Claim("name"), groups, null);
    }

    private async Task<OpenIdConnectConfiguration> ConfigurationAsync(EntraOptions o, CancellationToken ct)
    {
        if (_configuration is null)
        {
            await ConfigurationLock.WaitAsync(ct);
            try
            {
                _configuration ??= new ConfigurationManager<OpenIdConnectConfiguration>($"{o.Authority}/v2.0/.well-known/openid-configuration",
                    new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever { RequireHttps = true });
            }
            finally { ConfigurationLock.Release(); }
        }
        logger.LogDebug("Loading OpenID configuration for {Authority}", o.Authority);
        return await _configuration.GetConfigurationAsync(ct);
    }
}

/// <summary>Stand-in when Entra ID sign-in is not configured.</summary>
public sealed class DisabledIdentityProvider : IExternalIdentityProvider
{
    public bool Enabled => false;
    public string Name => "";
    public Task<string> BeginAsync(CancellationToken ct) => throw new BusinessRuleException("Entra ID sign-in is not configured");
    public Task<ExternalIdentity> CompleteAsync(string code, string state, CancellationToken ct) => throw new BusinessRuleException("Entra ID sign-in is not configured");
}
