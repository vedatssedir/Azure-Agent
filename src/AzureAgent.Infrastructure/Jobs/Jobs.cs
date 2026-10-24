using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TickerQ.Utilities.Base;
using Wolverine;

namespace AzureAgent.Infrastructure.Jobs;

/// <summary>
/// Recurring jobs (TickerQ). They only enqueue Wolverine messages, so running on several
/// instances at once is safe: sync is idempotent and runs start through the queued-status guard.
/// </summary>
public sealed class ScheduledJobs(IAppDbContext db, ITenantContext tenant, IMessageBus bus, IClock clock, ILogger<ScheduledJobs> logger)
{
    [TickerFunction("project-sync", "0 * * * * *")]
    public async Task SyncDueProjects(TickerFunctionContext context, CancellationToken ct)
    {
        tenant.UseAllOrganizations();
        var now = clock.UtcNow;
        var projects = await db.Projects.AsNoTracking().Select(p => new { p.Id, p.OrganizationId, p.LastSyncedAt, p.Settings.SyncIntervalMinutes }).ToListAsync(ct);
        var hasPat = (await db.Secrets.AsNoTracking().Where(s => s.Name == Domain.Projects.SecretNames.AzureDevOpsPat).Select(s => new { s.OrganizationId, s.ProjectId }).ToListAsync(ct));
        foreach (var project in projects)
        {
            if (!hasPat.Any(s => s.OrganizationId == project.OrganizationId && (s.ProjectId == project.Id || s.ProjectId == null))) continue;
            if (project.LastSyncedAt is { } last && now - last < TimeSpan.FromMinutes(project.SyncIntervalMinutes)) continue;
            await bus.PublishAsync(new SyncProject(project.OrganizationId, project.Id, "schedule"));
        }
    }

    [TickerFunction("run-watchdog", "0 */2 * * * *")]
    public async Task WatchRuns(TickerFunctionContext context, CancellationToken ct)
    {
        tenant.UseAllOrganizations();
        var now = clock.UtcNow;
        var running = await db.AgentRuns.Where(r => r.Status == RunStatus.Running)
            .Join(db.Agents, r => r.AgentId, a => a.Id, (r, a) => new { Run = r, a.Settings.TimeoutMinutes }).ToListAsync(ct);
        foreach (var x in running.Where(x => x.Run.StartedAt < now.AddMinutes(-(x.TimeoutMinutes + 10))))
        {
            x.Run.Fail(now, "Run was abandoned by its worker (timeout exceeded)");
            logger.LogWarning("Marked abandoned run {RunId} as failed", x.Run.Id);
        }
        await db.SaveChangesAsync(ct);
        // Queued runs whose message was lost or deferred (agent at capacity) are re-published.
        var stale = await db.AgentRuns.AsNoTracking().Where(r => r.Status == RunStatus.Queued && r.UpdatedAt < now.AddMinutes(-2))
            .Select(r => new { r.Id, r.OrganizationId }).Take(200).ToListAsync(ct);
        foreach (var run in stale) await bus.PublishAsync(new ExecuteAgentRun(run.OrganizationId, run.Id));
    }

    [TickerFunction("token-cleanup", "0 30 3 * * *")]
    public async Task CleanupTokens(TickerFunctionContext context, CancellationToken ct)
    {
        var cutoff = clock.UtcNow.AddDays(-30);
        var removed = await db.RefreshTokens.Where(t => t.ExpiresAt < cutoff || (t.RevokedAt != null && t.RevokedAt < cutoff)).ExecuteDeleteAsync(ct);
        logger.LogInformation("Removed {Count} expired refresh tokens", removed);
    }
}
