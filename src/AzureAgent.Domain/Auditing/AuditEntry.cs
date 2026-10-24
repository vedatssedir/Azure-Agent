using AzureAgent.Domain.Common;

namespace AzureAgent.Domain.Auditing;

/// <summary>Append-only record of every mutating action.</summary>
public sealed class AuditEntry : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; init; }
    public Guid? ProjectId { get; init; }
    public required string Actor { get; init; }
    public required string Action { get; init; }
    public required string EntityType { get; init; }
    public required string EntityId { get; init; }
    public string? Details { get; init; }
}
