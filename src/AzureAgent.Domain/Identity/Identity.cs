using AzureAgent.Domain.Common;

namespace AzureAgent.Domain.Identity;

public sealed class User : Entity
{
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    /// <summary>Null for accounts that only sign in through an external provider (Entra ID).</summary>
    public string? PasswordHash { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Instance-wide administrator (can create organizations, manage all users).</summary>
    public bool IsSystemAdmin { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
}

public sealed class RefreshToken : Entity
{
    public Guid UserId { get; init; }
    public required string TokenHash { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
    public string? CreatedByIp { get; init; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}

/// <summary>A tenant: one Azure DevOps organization connected to the platform.</summary>
public sealed class Organization : Entity
{
    public required string Name { get; set; }
    public required string Slug { get; init; }
    /// <summary>https://dev.azure.com/&lt;organization&gt;; everything (projects, repos, boards, people) is synced from here.</summary>
    public string? AzureOrganizationUrl { get; set; }
    /// <summary>Hard monthly spend limit for all agents of the organization (USD). Null = unlimited.</summary>
    public decimal? MonthlyBudgetUsd { get; set; }
    public Projects.OrganizationSettings Settings { get; set; } = new();
    public DateTimeOffset? LastSyncedAt { get; set; }
    public string? LastSyncError { get; set; }
}

public enum OrganizationRole
{
    Member = 0,
    Admin = 1,
    Owner = 2,
}

public sealed class OrganizationMember : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; init; }
    public Guid UserId { get; init; }
    public OrganizationRole Role { get; set; }
    public User? User { get; init; }
}

/// <summary>Link between a user and an external identity (Entra ID object id within a tenant).</summary>
public sealed class ExternalLogin : Entity
{
    public Guid UserId { get; init; }
    public required string Provider { get; init; }
    /// <summary>Stable subject: <c>{tenantId}:{objectId}</c> for Entra ID.</summary>
    public required string Subject { get; init; }
    public string? Email { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}

/// <summary>
/// Invitation to join an organization. The raw token is only in the invitation link; the
/// database keeps its hash. Accepting it creates the account (or links an existing one).
/// </summary>
public sealed class Invitation : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; init; }
    public required string Email { get; init; }
    public required string DisplayName { get; set; }
    public OrganizationRole Role { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? AcceptedByUserId { get; set; }
    public required string InvitedBy { get; init; }
    /// <summary>Project that triggered the invitation (Azure team sync), if any.</summary>
    public Guid? ProjectId { get; init; }

    public bool IsPending(DateTimeOffset now) => AcceptedAt is null && RevokedAt is null && ExpiresAt > now;
}

public enum AzureCredentialKind
{
    PersonalAccessToken = 0,
    /// <summary>Delegated Entra ID token (user_impersonation on Azure DevOps), refreshed automatically.</summary>
    OAuth = 1,
}

/// <summary>
/// A user's own Azure DevOps credential. Writes the user starts (assignments, new work items,
/// comments) run with it, so Azure history shows the real person and Azure's own permissions apply.
/// </summary>
public sealed class UserAzureCredential : Entity
{
    public Guid UserId { get; init; }
    public AzureCredentialKind Kind { get; init; }
    /// <summary>Azure DevOps organization URL the credential is for; null = every organization (OAuth).</summary>
    public string? AzureOrganizationUrl { get; init; }
    public required byte[] Ciphertext { get; set; }
    public byte[]? RefreshCiphertext { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>Identity Azure DevOps reported for the credential (verified on save).</summary>
    public required string AzureUniqueName { get; set; }
    public string? AzureDisplayName { get; set; }
    public DateTimeOffset VerifiedAt { get; set; }
    public string? LastError { get; set; }
}
