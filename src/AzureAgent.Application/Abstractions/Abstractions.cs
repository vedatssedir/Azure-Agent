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
