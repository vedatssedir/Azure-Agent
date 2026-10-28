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
    /// <summary>Give platform users who are members of the project's Azure teams a project membership on every sync.</summary>
    public bool AutoProvisionMembers { get; set; } = true;
    /// <summary>Create invitations for Azure team members who have no platform account yet.</summary>
    public bool AutoInviteTeamMembers { get; set; }
    /// <summary>Project role per Azure team (by name). Empty = every team maps to Developer.</summary>
    public List<TeamRoleMapping> TeamRoles { get; set; } = [];
    /// <summary>Project role per Entra ID group (object id), applied when members sign in with Entra ID.</summary>
    public List<GroupRoleMapping> GroupRoles { get; set; } = [];
    /// <summary>Require the user's own Azure credential for writes they start (no fallback to the service identity).</summary>
    public bool RequireUserCredentialForWrites { get; set; }
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
