using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Features.Audit;

public sealed record AuditEntryDto(DateTimeOffset At, string Actor, string Action, string EntityType, string EntityId, string? Details);

public sealed record ListAuditQuery(Guid ProjectId, int Take = 200);

public static class ListAuditHandler
{
    public static async Task<IReadOnlyList<AuditEntryDto>> Handle(ListAuditQuery query, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.AuditView, ct);
        return await db.AuditEntries.AsNoTracking().Where(a => a.ProjectId == query.ProjectId)
            .OrderByDescending(a => a.CreatedAt).Take(Math.Clamp(query.Take, 1, 1000))
            .Select(a => new AuditEntryDto(a.CreatedAt, a.Actor, a.Action, a.EntityType, a.EntityId, a.Details)).ToListAsync(ct);
    }
}
