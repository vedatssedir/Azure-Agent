using AzureAgent.Domain.Common;

namespace AzureAgent.Domain.Azure;

// Read models mirrored from Azure DevOps. Azure is the source of truth; these rows are
// overwritten by sync and only carry platform-specific additions (policies, links).

public sealed class Team : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; init; }
    public Guid ProjectId { get; init; }
    public required string AzureId { get; init; }
    public required string Name { get; set; }
    public List<TeamMember> Members { get; set; } = [];
    /// <summary>Area paths owned by the team (Azure team field values), used by the team board.</summary>
    public List<TeamArea> Areas { get; set; } = [];
    /// <summary>The team's Kanban boards (one per backlog level) with their columns as configured in Azure Boards.</summary>
    public List<TeamBoard> Boards { get; set; } = [];
    /// <summary>The team's sprints.</summary>
    public List<TeamIteration> Iterations { get; set; } = [];
}

public sealed class TeamBoard
{
    public required string Name { get; set; }
    /// <summary>Field that stores the board column (WEF_..._Kanban.Column); written when columns share a state.</summary>
    public string? ColumnField { get; set; }
    public List<BoardColumn> Columns { get; set; } = [];
}

public sealed class BoardColumn
{
    public required string Name { get; set; }
    /// <summary>incoming, inProgress or outgoing.</summary>
    public string ColumnType { get; set; } = "inProgress";
    /// <summary>WIP limit (0 = none).</summary>
    public int ItemLimit { get; set; }
    public List<BoardStateMapping> StateMappings { get; set; } = [];
}

public sealed class BoardStateMapping
{
    public required string WorkItemType { get; set; }
    public required string State { get; set; }
}

public sealed class TeamIteration
{
    public required string AzureId { get; set; }
    public required string Name { get; set; }
    public required string Path { get; set; }
    public DateTimeOffset? StartDate { get; set; }
    public DateTimeOffset? FinishDate { get; set; }
    /// <summary>past, current or future.</summary>
    public string TimeFrame { get; set; } = "";
}

public sealed class TeamArea
{
    public required string Path { get; set; }
    public bool IncludeChildren { get; set; }
}

public sealed class TeamMember
{
    public required string UniqueName { get; set; }
    public required string DisplayName { get; set; }
}

public sealed class Repository : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; init; }
    public Guid ProjectId { get; init; }
    public required string AzureId { get; init; }
    public required string Name { get; set; }
    public string? DefaultBranch { get; set; }
    public required string RemoteUrl { get; set; }
    public required string WebUrl { get; set; }
    public RepositoryPolicy Policy { get; set; } = new();
    public long SizeBytes { get; set; }
    public bool IsRemoved { get; set; }
    /// <summary>Last successful refresh of the local git mirror (code browser, history).</summary>
    public DateTimeOffset? MirrorSyncedAt { get; set; }
    public string? MirrorError { get; set; }
}

/// <summary>Platform rules for agents in one repository.</summary>
public sealed class RepositoryPolicy
{
    public bool AgentsEnabled { get; set; } = true;
    /// <summary>Branches agents may never push to (defaults to the repository's default branch).</summary>
    public List<string> ProtectedBranches { get; set; } = ["main", "master", "develop", "release/*"];
    public bool AutoReview { get; set; } = true;
    /// <summary>Commands agents may run in this repository (build, test, lint).</summary>
    public List<string> AllowedCommands { get; set; } = ["dotnet", "npm", "pnpm", "yarn", "node", "python", "pytest", "go", "mvn", "gradle"];
    public int MaxFilesChangedPerRun { get; set; } = 50;
}

public enum AccessEffect
{
    Allow = 0,
    Deny = 1,
}

/// <summary>Repository-level permission override for a user or for everyone with a project role.</summary>
public sealed class RepositoryAccessRule : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; init; }
    public Guid RepositoryId { get; init; }
    public Guid? UserId { get; init; }
    public Projects.ProjectRole? Role { get; init; }
    public required string Permission { get; init; }
    public AccessEffect Effect { get; init; }
}

public sealed class WorkItem : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; init; }
    public Guid ProjectId { get; init; }
    public int AzureId { get; init; }
    public int Revision { get; set; }
    public required string Type { get; set; }
    public required string Title { get; set; }
    public required string State { get; set; }
    public string? AssignedToUniqueName { get; set; }
    public string? AssignedToDisplayName { get; set; }
    /// <summary>Kanban column on the owning team's board (System.BoardColumn).</summary>
    public string? BoardColumn { get; set; }
    public string? CreatedByDisplayName { get; set; }
    public DateTimeOffset? AzureCreatedAt { get; set; }
    public string? AreaPath { get; set; }
    public string? IterationPath { get; set; }
    public int? Priority { get; set; }
    public double? StoryPoints { get; set; }
    public int? ParentAzureId { get; set; }
    public List<string> Tags { get; set; } = [];
    /// <summary>Repositories named by <c>repo:&lt;name&gt;</c> tags.</summary>
    public List<string> RepositoryNames { get; set; } = [];
    public string Description { get; set; } = "";
    public string AcceptanceCriteria { get; set; } = "";
    public required string WebUrl { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}

public sealed class Commit : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; init; }
    public Guid RepositoryId { get; init; }
    public required string CommitId { get; init; }
    public required string AuthorName { get; init; }
    public string? AuthorEmail { get; init; }
    public required string Message { get; init; }
    public DateTimeOffset CommittedAt { get; init; }
    public required string WebUrl { get; init; }
    /// <summary>Work items referenced as AB#123 in the message.</summary>
    public List<int> WorkItemAzureIds { get; init; } = [];
}

public enum PullRequestStatus
{
    Active = 0,
    Completed = 1,
    Abandoned = 2,
}

public sealed class PullRequest : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; init; }
    public Guid RepositoryId { get; init; }
    public int AzureId { get; init; }
    public required string Title { get; set; }
    public PullRequestStatus Status { get; set; }
    public required string SourceBranch { get; set; }
    public required string TargetBranch { get; set; }
    public required string CreatedBy { get; set; }
    public bool CreatedByAgent { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public required string WebUrl { get; set; }
    public List<int> WorkItemAzureIds { get; set; } = [];
    public string? LastReviewedSourceCommit { get; set; }
}
