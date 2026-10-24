namespace AzureAgent.Application.Abstractions;

public sealed record AzureIdentity(string DisplayName, string UniqueName);

public sealed record AzureTeam(string Id, string Name);

public sealed record AzureRepository(string Id, string Name, string? DefaultBranch, string RemoteUrl, string WebUrl);

public sealed record AzureWorkItem(int Id, int Rev, IReadOnlyDictionary<string, object?> Fields);

public sealed record AzureCommit(string CommitId, string AuthorName, string? AuthorEmail, DateTimeOffset Date, string Comment);

public sealed record AzurePullRequest(int Id, string Title, string Status, string SourceRefName, string TargetRefName, string CreatedBy,
    DateTimeOffset CreationDate, DateTimeOffset? ClosedDate, string? LastMergeSourceCommit, string RepositoryName);

public sealed record AzureFileChange(string Path, string ChangeType);

public sealed record AzureConnectionInfo(string OrganizationUrl, string Project, string PersonalAccessToken);

/// <summary>Azure DevOps REST operations used by the platform (api-version 7.1).</summary>
public interface IAzureDevOpsClient
{
    Task<string> GetProjectNameAsync(CancellationToken ct);
    Task<IReadOnlyList<AzureTeam>> ListTeamsAsync(CancellationToken ct);
    Task<IReadOnlyList<AzureIdentity>> ListTeamMembersAsync(string teamId, CancellationToken ct);
    Task<IReadOnlyList<AzureRepository>> ListRepositoriesAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> ListRootFilesAsync(string repository, CancellationToken ct);
    Task<IReadOnlyList<int>> QueryWorkItemIdsAsync(string wiql, CancellationToken ct);
    Task<IReadOnlyList<AzureWorkItem>> GetWorkItemsAsync(IReadOnlyCollection<int> ids, CancellationToken ct);
    Task<AzureWorkItem> UpdateWorkItemFieldsAsync(int id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct);
    Task AddWorkItemCommentAsync(int id, string html, CancellationToken ct);
    Task<IReadOnlyList<AzureCommit>> ListCommitsAsync(string repository, DateTimeOffset? since, CancellationToken ct);
    Task<IReadOnlyList<AzurePullRequest>> ListPullRequestsAsync(string repository, CancellationToken ct);
    Task<IReadOnlyList<int>> GetPullRequestWorkItemIdsAsync(string repository, int pullRequestId, CancellationToken ct);
    Task<AzurePullRequest> CreatePullRequestAsync(string repository, string sourceBranch, string targetBranch, string title, string description,
        int? workItemId, CancellationToken ct);
    Task<IReadOnlyList<AzureFileChange>> GetPullRequestChangesAsync(string repository, int pullRequestId, CancellationToken ct);
    Task<string?> GetFileContentAsync(string repository, string path, string commitId, CancellationToken ct);
    Task AddPullRequestThreadAsync(string repository, int pullRequestId, string content, string? filePath, int? line, CancellationToken ct);
    string WorkItemWebUrl(int id);
    string PullRequestWebUrl(string repositoryWebUrl, int pullRequestId);
}

public interface IAzureDevOpsClientFactory
{
    IAzureDevOpsClient Create(AzureConnectionInfo connection);
}
