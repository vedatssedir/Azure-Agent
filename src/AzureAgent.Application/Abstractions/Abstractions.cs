using AzureAgent.Domain.Agents;
using AzureAgent.Domain.Auditing;
using AzureAgent.Domain.Azure;
using AzureAgent.Domain.Costs;
using AzureAgent.Domain.Identity;
using AzureAgent.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Abstractions;

public interface IAppDbContext
{
    DbSet<User> Users { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<ExternalLogin> ExternalLogins { get; }
    DbSet<Invitation> Invitations { get; }
    DbSet<UserAzureCredential> UserAzureCredentials { get; }
    DbSet<Organization> Organizations { get; }
    DbSet<OrganizationMember> OrganizationMembers { get; }
    DbSet<Project> Projects { get; }
    DbSet<ProjectMember> ProjectMembers { get; }
    DbSet<Secret> Secrets { get; }
    DbSet<Team> Teams { get; }
    DbSet<Repository> Repositories { get; }
    DbSet<RepositoryAccessRule> RepositoryAccessRules { get; }
    DbSet<WorkItem> WorkItems { get; }
    DbSet<Commit> Commits { get; }
    DbSet<PullRequest> PullRequests { get; }
    DbSet<Agent> Agents { get; }
    DbSet<Skill> Skills { get; }
    DbSet<McpServer> McpServers { get; }
    DbSet<AgentRun> AgentRuns { get; }
    DbSet<AgentRunStep> AgentRunSteps { get; }
    DbSet<CodeReview> CodeReviews { get; }
    DbSet<ModelPrice> ModelPrices { get; }
    DbSet<AuditEntry> AuditEntries { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Authenticated caller. Empty for background work.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    string? Email { get; }
    bool IsSystemAdmin { get; }
    bool IsAuthenticated => UserId is not null;
    string ActorName => Email ?? "system";
}

/// <summary>
/// Organization the current unit of work is scoped to. HTTP requests take it from the
/// X-Organization-Id header (verified against membership); background jobs set it explicitly.
/// </summary>
public interface ITenantContext
{
    Guid? OrganizationId { get; }
    /// <summary>Background jobs that iterate every organization bypass the tenant filter.</summary>
    bool BypassFilter { get; }
    void Use(Guid organizationId);
    void UseAllOrganizations();
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);
}

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

public interface ITokenService
{
    AccessToken CreateAccessToken(User user);
    /// <summary>Returns the raw refresh token (sent to the client) and its hash (stored).</summary>
    (string Token, string Hash) CreateRefreshToken();
    string HashRefreshToken(string token);
    TimeSpan RefreshTokenLifetime { get; }
}

public interface ISecretProtector
{
    byte[] Protect(string plaintext);
    string Unprotect(byte[] ciphertext);
}

public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default);
    Task RemoveAsync(string key, CancellationToken ct = default);
}

public sealed record OAuthToken(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>Entra ID tokens for Azure DevOps (resource 499b84ac-1321-427f-aa17-267ca6975798).</summary>
public interface IAzureTokenProvider
{
    /// <summary>Client-credentials token for a service principal (cached until shortly before expiry).</summary>
    Task<OAuthToken> GetServicePrincipalTokenAsync(string tenantId, string clientId, string clientSecret, CancellationToken ct);
    /// <summary>Redeems a delegated refresh token obtained at Entra ID sign-in.</summary>
    Task<OAuthToken> RefreshUserTokenAsync(string refreshToken, CancellationToken ct);
}

/// <summary>A person authenticated by an external provider, with optional delegated Azure DevOps access.</summary>
public sealed record ExternalIdentity(
    string Provider,
    string Subject,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Groups,
    OAuthToken? AzureDevOpsToken);

/// <summary>OpenID Connect sign-in (authorization code + PKCE) with Entra ID.</summary>
public interface IExternalIdentityProvider
{
    bool Enabled { get; }
    string Name { get; }
    /// <summary>Returns the URL to redirect the browser to; state, nonce and the PKCE verifier are kept server side.</summary>
    Task<string> BeginAsync(CancellationToken ct);
    /// <summary>Redeems the code for the state issued by <see cref="BeginAsync"/> and validates the ID token.</summary>
    Task<ExternalIdentity> CompleteAsync(string code, string state, CancellationToken ct);
}

public interface IEmailSender
{
    bool Enabled { get; }
    Task SendAsync(string to, string subject, string html, CancellationToken ct);
}

/// <summary>Public base URL of the web app (for links in invitations and e-mails).</summary>
public interface IAppLinks
{
    string InvitationUrl(string token);
}
