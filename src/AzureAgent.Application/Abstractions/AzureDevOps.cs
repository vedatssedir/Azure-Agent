namespace AzureAgent.Application.Abstractions;

public sealed record AzureIdentity(string DisplayName, string UniqueName);

public sealed record AzureTeam(string Id, string Name);

public sealed record AzureRepository(string Id, string Name, string? DefaultBranch, string RemoteUrl, string WebUrl);

public sealed record AzureWorkItem(int Id, int Rev, IReadOnlyDictionary<string, object?> Fields);

public sealed record AzureCommit(string CommitId, string AuthorName, string? AuthorEmail, DateTimeOffset Date, string Comment);

public sealed record AzurePullRequest(int Id, string Title, string Status, string SourceRefName, string TargetRefName, string CreatedBy,
    DateTimeOffset CreationDate, DateTimeOffset? ClosedDate, string? LastMergeSourceCommit, string RepositoryName);

public sealed record AzureFileChange(string Path, string ChangeType);

/// <summary>How a call authenticates to Azure DevOps: a PAT (HTTP Basic) or an Entra ID access token (Bearer).</summary>
public sealed record AzureCredential(string Scheme, string Token, string Owner)
{
    public static AzureCredential Pat(string pat, string owner = "service") => new("Basic", pat, owner);
    public static AzureCredential Bearer(string accessToken, string owner = "service") => new("Bearer", accessToken, owner);

    public bool IsBearer => Scheme == "Bearer";

    /// <summary>Value of the Authorization header (also used by git as http.extraHeader).</summary>
    public string HeaderValue => IsBearer
        ? $"Bearer {Token}"
        : $"Basic {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($":{Token}"))}";

    // Keep tokens out of logs and exception messages.
    public override string ToString() => $"{Scheme} credential of {Owner}";
}

public sealed record AzureConnectionInfo(string OrganizationUrl, string Project, AzureCredential Credential);

public sealed record AzureWorkItemType(string Name, IReadOnlyList<string> States, IReadOnlyList<string> Fields);

/// <summary>Azure DevOps REST operations used by the platform (api-version 7.1).</summary>
public interface IAzureDevOpsClient
{
    Task<string> GetProjectNameAsync(CancellationToken ct);
    Task<IReadOnlyList<AzureTeam>> ListTeamsAsync(CancellationToken ct);
    Task<IReadOnlyList<AzureIdentity>> ListTeamMembersAsync(string teamId, CancellationToken ct);
    /// <summary>Area paths of a team (team field values).</summary>
    Task<IReadOnlyList<(string Path, bool IncludeChildren)>> ListTeamAreasAsync(string teamId, CancellationToken ct);
    /// <summary>The identity the credential authenticates as.</summary>
    Task<AzureIdentity> GetAuthenticatedIdentityAsync(CancellationToken ct);
    Task<IReadOnlyList<AzureWorkItemType>> ListWorkItemTypesAsync(CancellationToken ct);
    Task<AzureWorkItem> CreateWorkItemAsync(string type, IReadOnlyDictionary<string, object?> fields, CancellationToken ct);
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
