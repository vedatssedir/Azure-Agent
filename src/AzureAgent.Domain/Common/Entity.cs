namespace AzureAgent.Domain.Common;

public abstract class Entity
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Rows owned by one organization (tenant). EF applies a tenant query filter to these.</summary>
public interface IOrganizationScoped
{
    Guid OrganizationId { get; }
}

public sealed class DomainException(string message) : Exception(message);
