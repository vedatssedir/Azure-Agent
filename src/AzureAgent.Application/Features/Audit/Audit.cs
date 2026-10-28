using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Features.Audit;

public sealed record AuditEntryDto(DateTimeOffset At, string Actor, string Action, string EntityType, string EntityId, string? Details, Guid? ProjectId);

/// <summary>The organization's audit log (organization admins).</summary>
public sealed record ListAuditQuery(Guid? ProjectId = null, int Take = 300);

public static class ListAuditHandler
{
    public static async Task<IReadOnlyList<AuditEntryDto>> Handle(ListAuditQuery query, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        return await db.AuditEntries.AsNoTracking().Where(a => query.ProjectId == null || a.ProjectId == query.ProjectId)
            .OrderByDescending(a => a.CreatedAt).Take(Math.Clamp(query.Take, 1, 1000))
            .Select(a => new AuditEntryDto(a.CreatedAt, a.Actor, a.Action, a.EntityType, a.EntityId, a.Details, a.ProjectId)).ToListAsync(ct);
    }
}
