using AzureAgent.Domain.Common;
using AzureAgent.Domain.Identity;

namespace AzureAgent.Domain.Projects;

/// <summary>
/// One Azure DevOps project, mirrored from the organization the platform is connected to. Projects
/// are never created by hand: organization sync adds, updates and retires them.
/// </summary>
public sealed class Project : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; init; }
    /// <summary>Azure DevOps project id.</summary>
    public required string AzureId { get; init; }
    public required string Name { get; set; }
    /// <summary>Short uppercase key, unique in the organization (derived from the name).</summary>
    public required string Key { get; init; }
    public string? Description { get; set; }
    public required string AzureOrganizationUrl { get; set; }
    public required string AzureProjectName { get; set; }
    /// <summary>Work item types of the project's process, with their states (colors, categories) and fields.</summary>
    public List<WorkItemTypeInfo> WorkItemTypes { get; set; } = [];
    public decimal? MonthlyBudgetUsd { get; set; }
    /// <summary>The project no longer exists in Azure DevOps (kept for history).</summary>
    public bool IsRemoved { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public string? LastSyncError { get; set; }

    /// <summary>A state is closed when its category is Completed or Removed (process independent).</summary>
    public bool IsClosedState(string type, string state) =>
        WorkItemTypes.FirstOrDefault(t => t.Name == type)?.States.FirstOrDefault(s => s.Name == state)?.Category is "Completed" or "Removed"
        || (WorkItemTypes.Count == 0 && state is "Closed" or "Done" or "Removed" or "Completed");

    /// <summary>First state of the type in the given category (Proposed, InProgress, Resolved, Completed, Removed).</summary>
    public string? StateInCategory(string type, string category) =>
        WorkItemTypes.FirstOrDefault(t => t.Name == type)?.States.FirstOrDefault(s => s.Category == category)?.Name;
}

public sealed class WorkItemTypeInfo
{
    public required string Name { get; set; }
    public string Color { get; set; } = "";
    public bool IsDisabled { get; set; }
    public List<WorkItemStateInfo> States { get; set; } = [];
    public List<string> Fields { get; set; } = [];
}

public sealed class WorkItemStateInfo
{
    public required string Name { get; set; }
    public string Color { get; set; } = "";
    /// <summary>Proposed, InProgress, Resolved, Completed or Removed.</summary>
    public string Category { get; set; } = "";
}

/// <summary>
/// Organization-wide behaviour, edited in one place (Settings). There are no per-project settings:
/// every synced project follows these.
/// </summary>
public sealed class OrganizationSettings
{
    /// <summary>Start the developer's default agent when a work item is assigned to them (items tagged agent:&lt;name&gt; always start).</summary>
    public bool AutoDispatch { get; set; } = true;
    /// <summary>Require a human to approve a run before it starts.</summary>
    public bool RequireRunApproval { get; set; }
    /// <summary>Move the work item to its In Progress state when an agent starts and to Resolved (if the process has it) when the PR opens.</summary>
    public bool UpdateWorkItemState { get; set; } = true;
    /// <summary>Run an AI code review on every new pull request in repositories that allow it.</summary>
    public bool AutoReviewPullRequests { get; set; } = true;
    public int SyncIntervalMinutes { get; set; } = 10;

    /// <summary>Give platform users who are in an Azure team a membership of that team's project on every sync.</summary>
    public bool AutoProvisionMembers { get; set; } = true;
    /// <summary>Create invitations for Azure team members who have no platform account yet.</summary>
    public bool AutoInviteTeamMembers { get; set; }
    /// <summary>Developers of the Azure organization may create their own account (identity proven with their Azure PAT).</summary>
    public bool AllowSelfSignUp { get; set; } = true;
    /// <summary>Give every new developer a default agent built from the AI defaults below.</summary>
    public bool CreateDefaultAgentForDevelopers { get; set; } = true;
    /// <summary>Require the user's own Azure credential for writes they start (no fallback to the service identity).</summary>
    public bool RequireUserCredentialForWrites { get; set; }
    /// <summary>Project role per Azure team name. Empty = every team maps to Developer.</summary>
    public List<TeamRoleMapping> TeamRoles { get; set; } = [];
    /// <summary>Project role per Entra ID group (object id), applied when members sign in with Entra ID.</summary>
    public List<GroupRoleMapping> GroupRoles { get; set; } = [];

    /// <summary>Defaults for new agents.</summary>
    public Agents.AgentRuntime DefaultRuntime { get; set; } = Agents.AgentRuntime.ClaudeCode;
    public Agents.ModelProvider DefaultProvider { get; set; } = Agents.ModelProvider.Anthropic;
    public string DefaultModel { get; set; } = "claude-sonnet-4-5";
    /// <summary>Azure OpenAI resource endpoint (https://&lt;resource&gt;.openai.azure.com).</summary>
    public string? AzureOpenAiEndpoint { get; set; }
}

public sealed class TeamRoleMapping
{
    public string Team { get; set; } = "";
    public ProjectRole Role { get; set; } = ProjectRole.Developer;
}

public sealed class GroupRoleMapping
{
    public string GroupId { get; set; } = "";
    public ProjectRole Role { get; set; } = ProjectRole.Developer;
}

public enum MembershipSource
{
    Manual = 0,
    /// <summary>Created by Azure team sync; removed when the person leaves every mapped team.</summary>
    AzureTeam = 1,
    /// <summary>Created from an Entra ID group claim at sign-in.</summary>
    EntraGroup = 2,
}

public sealed class ProjectMember : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; init; }
    public Guid ProjectId { get; init; }
    public Guid UserId { get; init; }
    public ProjectRole Role { get; set; }
    /// <summary>Azure DevOps identity (uniqueName) of this person, used to match assignments.</summary>
    public string? AzureUniqueName { get; set; }
    public MembershipSource Source { get; set; }
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
    /// <summary>Claude Code subscription token (<c>claude setup-token</c>); used instead of ANTHROPIC_API_KEY by Claude Code agents when set.</summary>
    public const string ClaudeCodeOAuthToken = "CLAUDE_CODE_OAUTH_TOKEN";
    public const string CursorApiKey = "CURSOR_API_KEY";
    // Service principal (Entra ID app) used instead of the PAT when all three are set.
    public const string ServicePrincipalTenantId = "AZURE_DEVOPS_SP_TENANT_ID";
    public const string ServicePrincipalClientId = "AZURE_DEVOPS_SP_CLIENT_ID";
    public const string ServicePrincipalClientSecret = "AZURE_DEVOPS_SP_CLIENT_SECRET";
}
