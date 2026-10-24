namespace AzureAgent.Application.Features.Azure;

// Messages carry the organization id so handlers can scope their unit of work to the tenant.

/// <summary>Full reconciliation of one project with Azure DevOps.</summary>
public sealed record SyncProject(Guid OrganizationId, Guid ProjectId, string Trigger);

/// <summary>Refresh one work item (Service Hook path).</summary>
public sealed record SyncWorkItem(Guid OrganizationId, Guid ProjectId, int WorkItemAzureId);

/// <summary>Execute one queued agent run. Routed to the "agent-runs" RabbitMQ queue in production.</summary>
public sealed record ExecuteAgentRun(Guid OrganizationId, Guid RunId);

public sealed record SyncResult(int Teams, int Repositories, int WorkItems, int Commits, int PullRequests, int QueuedRuns);
