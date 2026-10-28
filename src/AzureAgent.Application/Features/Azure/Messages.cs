namespace AzureAgent.Application.Features.Azure;

// Messages carry the organization id so handlers can scope their unit of work to the tenant.

/// <summary>Reconciles the organization's project list, then queues a sync of every project.</summary>
public sealed record SyncOrganization(Guid OrganizationId, string Trigger);

/// <summary>Full reconciliation of one project with Azure DevOps.</summary>
public sealed record SyncProject(Guid OrganizationId, Guid ProjectId, string Trigger);

/// <summary>Refresh one work item (Service Hook path).</summary>
public sealed record SyncWorkItem(Guid OrganizationId, Guid ProjectId, int WorkItemAzureId);

/// <summary>Execute one queued agent run. Routed to the "agent-runs" RabbitMQ queue in production.</summary>
public sealed record ExecuteAgentRun(Guid OrganizationId, Guid RunId);

public sealed record SyncResult(int Teams, int Repositories, int WorkItems, int Commits, int PullRequests, int QueuedRuns,
    int MembersAdded = 0, int MembersRemoved = 0, int Invited = 0);

public sealed record OrganizationSyncResult(int Projects, int Added, int Removed);
