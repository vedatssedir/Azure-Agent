using AzureAgent.Domain.Common;

namespace AzureAgent.Domain.Identity;

public sealed class User : Entity
{
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public required string PasswordHash { get; set; }
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

public sealed class Organization : Entity
{
    public required string Name { get; set; }
    public required string Slug { get; init; }
    /// <summary>Hard monthly spend limit for all agents of the organization (USD). Null = unlimited.</summary>
    public decimal? MonthlyBudgetUsd { get; set; }
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
