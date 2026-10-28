using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Application.Features.Secrets;
using AzureAgent.Domain.Projects;
using AzureAgent.Infrastructure.Persistence;
using Carter;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace AzureAgent.Api.Modules;

/// <summary>
/// Azure DevOps Service Hooks (Web Hooks), one URL per organization. Authenticated with HTTP Basic:
/// any user name, password = the organization's service hook password (Settings → Azure DevOps).
/// Events name their project; work item events refresh one item, git events sync the project.
/// </summary>
public sealed class ServiceHooksModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/hooks/{organizationId:guid}", async (Guid organizationId, HttpContext http, AppDbContext db, ITenantContext tenant, SecretResolver secrets,
            IMessageBus bus, ILogger<ServiceHooksModule> logger, CancellationToken ct) =>
        {
            tenant.Use(organizationId);
            var expected = await secrets.GetOrganizationAsync(organizationId, SecretNames.ServiceHookPassword, ct);
            if (expected is null || !PasswordMatches(http.Request.Headers.Authorization.ToString(), expected)) return Results.Unauthorized();

            using var payload = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct);
            var azureProjectId = payload.RootElement.TryGetProperty("resourceContainers", out var containers) && containers.TryGetProperty("project", out var p)
                && p.TryGetProperty("id", out var id) ? id.GetString() : null;
            var project = azureProjectId is null ? null : await db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.AzureId == azureProjectId, ct);
            var eventType = payload.RootElement.TryGetProperty("eventType", out var e) ? e.GetString() ?? "" : "";
            if (project is null)
            {
                // A project created after the last sync: pick it up with an organization sync.
                await bus.PublishAsync(new SyncOrganization(organizationId, eventType));
                return Results.Accepted();
            }
            logger.LogInformation("Service hook {EventType} for project {Project}", eventType, project.Key);
            if (eventType.StartsWith("workitem.", StringComparison.Ordinal) && WorkItemId(payload.RootElement) is { } workItemId)
                await bus.PublishAsync(new SyncWorkItem(project.OrganizationId, project.Id, workItemId));
            else if (eventType.StartsWith("git.", StringComparison.Ordinal))
                await bus.PublishAsync(new SyncProject(project.OrganizationId, project.Id, eventType));
            return Results.Accepted();
        }).AllowAnonymous().WithTags("Service hooks").RequireRateLimiting("hooks");
    }

    private static bool PasswordMatches(string header, string expected)
    {
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim())); }
        catch (FormatException) { return false; }
        var password = decoded[(decoded.IndexOf(':') + 1)..];
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(expected));
    }

    private static int? WorkItemId(JsonElement root)
    {
        if (!root.TryGetProperty("resource", out var resource)) return null;
        foreach (var name in new[] { "workItemId", "id" })
        {
            if (resource.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number) return value.GetInt32();
        }
        return null;
    }
}
