using AzureAgent.Domain.Common;
using AzureAgent.Domain.Identity;

namespace AzureAgent.Domain.Projects;

/// <summary>One Azure DevOps project managed by the platform.</summary>
public sealed class Project : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; init; }
    public required string Name { get; set; }
    /// <summary>Short uppercase key, unique in the organization (e.g. SHOP).</summary>
    public required string Key { get; init; }
    public string? Description { get; set; }
    public required string AzureOrganizationUrl { get; set; }
    public required string AzureProjectName { get; set; }
    public ProjectSettings Settings { get; set; } = new();
    public decimal? MonthlyBudgetUsd { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public string? LastSyncError { get; set; }
}

/// <summary>Per-project behaviour; editable by holders of <see cref="Permission.SettingsManage"/>.</summary>
public sealed class ProjectSettings
{
    /// <summary>Queue an agent run when a work item is assigned to a developer paired with an agent.</summary>
    public bool AutoDispatch { get; set; } = true;
    /// <summary>Require a human to approve a queued run before it starts.</summary>
    public bool RequireRunApproval { get; set; }
    /// <summary>Work item state written when the agent opens a pull request (null = leave unchanged).</summary>
    public string? ReviewState { get; set; } = "Resolved";
    /// <summary>States in which work items are never dispatched.</summary>
    public List<string> ClosedStates { get; set; } = ["Closed", "Done", "Removed", "Resolved", "Completed"];
    /// <summary>Run an AI code review on every new pull request in repositories that allow it.</summary>
    public bool AutoReviewPullRequests { get; set; } = true;
    public int SyncIntervalMinutes { get; set; } = 5;
}

public sealed class ProjectMember : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; init; }
    public Guid ProjectId { get; init; }
    public Guid UserId { get; init; }
    public ProjectRole Role { get; set; }
    /// <summary>Azure DevOps identity (uniqueName) of this person, used to match assignments.</summary>
    public string? AzureUniqueName { get; set; }
    public User? User { get; init; }
}

/// <summary>Encrypted secret (PAT, model API keys, MCP credentials). Values are never returned by the API.</summary>
public sealed class Secret : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; init; }
    /// <summary>Null = organization-wide.</summary>
    public Guid? ProjectId { get; init; }
    public required string Name { get; init; }
    public required byte[] Ciphertext { get; set; }
}

public static class SecretNames
{
    public const string AzureDevOpsPat = "AZURE_DEVOPS_PAT";
    public const string ServiceHookPassword = "AZURE_SERVICE_HOOK_PASSWORD";
    public const string OpenAiApiKey = "OPENAI_API_KEY";
    public const string AzureOpenAiApiKey = "AZURE_OPENAI_API_KEY";
    public const string AnthropicApiKey = "ANTHROPIC_API_KEY";
    public const string GeminiApiKey = "GEMINI_API_KEY";
}
