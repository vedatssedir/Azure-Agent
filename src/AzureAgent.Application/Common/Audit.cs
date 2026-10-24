using System.Text.Json;
using AzureAgent.Application.Abstractions;
using AzureAgent.Domain.Auditing;

namespace AzureAgent.Application.Common;

public static class AuditExtensions
{
    /// <summary>Stages an audit row; it is saved with the caller's unit of work.</summary>
    public static void Audit(this IAppDbContext db, ICurrentUser user, Guid organizationId, string action, string entityType,
        object entityId, object? details = null, Guid? projectId = null)
    {
        db.AuditEntries.Add(new AuditEntry
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            Actor = user.ActorName,
            Action = action,
            EntityType = entityType,
            EntityId = entityId.ToString()!,
            Details = details is null ? null : JsonSerializer.Serialize(details),
        });
    }
}
