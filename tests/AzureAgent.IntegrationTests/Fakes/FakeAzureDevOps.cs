using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AzureAgent.Application.Abstractions;

namespace AzureAgent.IntegrationTests.Fakes;

/// <summary>Fake Azure DevOps organization: one isolated in-memory project per project name.</summary>
public sealed class FakeAzureDevOpsFactory : IAzureDevOpsClientFactory
{
    private readonly ConcurrentDictionary<string, FakeAzureDevOps> _projects = new(StringComparer.OrdinalIgnoreCase);

    public FakeAzureDevOps Project(string name) => _projects.GetOrAdd(name, _ => new FakeAzureDevOps());

    public IAzureDevOpsClient Create(AzureConnectionInfo connection) => Project(connection.Project).Create(connection);
}

/// <summary>In-memory Azure DevOps project. Repositories point at local bare git repositories.</summary>
public sealed class FakeAzureDevOps : IAzureDevOpsClient
{
    public ConcurrentDictionary<int, (int Rev, Dictionary<string, object?> Fields)> WorkItems { get; } = new();
    public List<AzureTeam> Teams { get; } = [new("team-1", "Backend")];
    public List<AzureIdentity> Members { get; } = [new("Jane Doe", "Jane@Contoso.com"), new("Joe Smith", "joe@contoso.com")];
    public List<AzureRepository> Repositories { get; } = [];
    public List<AzureCommit> Commits { get; } = [];
    public List<(AzurePullRequest Pr, int? WorkItemId)> PullRequests { get; } = [];
    public List<(int WorkItemId, string Html)> Comments { get; } = [];
    public List<(int PullRequestId, string Content, string? File, int? Line)> Threads { get; } = [];
    public Dictionary<string, string> FileContents { get; } = [];
    public string? RequiredPat { get; set; }

    public IAzureDevOpsClient Create(AzureConnectionInfo connection)
    {
        if (RequiredPat is not null && connection.PersonalAccessToken != RequiredPat) throw new InvalidOperationException("Azure DevOps rejected the PAT (HTTP 401)");
        return this;
    }

    public void AddWorkItem(int id, string title, string state, string? assignee = null, string tags = "")
    {
        var fields = new Dictionary<string, object?>
        {
            ["System.Title"] = JsonValue.Create(title),
            ["System.State"] = JsonValue.Create(state),
            ["System.WorkItemType"] = JsonValue.Create("User Story"),
            ["System.Tags"] = JsonValue.Create(tags),
            ["System.Description"] = JsonValue.Create($"<p>{title} – details</p>"),
            ["System.ChangedDate"] = JsonValue.Create(DateTimeOffset.UtcNow.ToString("O")),
        };
        if (assignee is not null) fields["System.AssignedTo"] = Identity(assignee);
        WorkItems[id] = (1, fields);
    }

    private JsonObject Identity(string uniqueName)
    {
        var member = Members.FirstOrDefault(m => m.UniqueName.Equals(uniqueName, StringComparison.OrdinalIgnoreCase)) ?? new AzureIdentity(uniqueName, uniqueName);
        return new JsonObject { ["displayName"] = member.DisplayName, ["uniqueName"] = member.UniqueName };
    }

    public Task<string> GetProjectNameAsync(CancellationToken ct) => Task.FromResult("Shop");
    public Task<IReadOnlyList<AzureTeam>> ListTeamsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AzureTeam>>(Teams);
    public Task<IReadOnlyList<AzureIdentity>> ListTeamMembersAsync(string teamId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AzureIdentity>>(Members);
    public Task<IReadOnlyList<AzureRepository>> ListRepositoriesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AzureRepository>>(Repositories);
    public Task<IReadOnlyList<string>> ListRootFilesAsync(string repository, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["Shop.sln", "README.md"]);
    public Task<IReadOnlyList<int>> QueryWorkItemIdsAsync(string wiql, CancellationToken ct) => Task.FromResult<IReadOnlyList<int>>(WorkItems.Keys.ToList());

    public Task<IReadOnlyList<AzureWorkItem>> GetWorkItemsAsync(IReadOnlyCollection<int> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AzureWorkItem>>(ids.Where(WorkItems.ContainsKey)
            .Select(id => new AzureWorkItem(id, WorkItems[id].Rev, WorkItems[id].Fields.ToDictionary(f => f.Key, f => f.Value is JsonNode n ? (object?)n.DeepClone() : f.Value))).ToList());

    public Task<AzureWorkItem> UpdateWorkItemFieldsAsync(int id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct)
    {
        var (rev, current) = WorkItems[id];
        foreach (var (key, value) in fields)
        {
            current[key] = key == "System.AssignedTo"
                ? (value is string s && s.Length > 0 ? Identity(s) : null)
                : JsonValue.Create(value?.ToString());
        }
        current["System.ChangedDate"] = JsonValue.Create(DateTimeOffset.UtcNow.ToString("O"));
        WorkItems[id] = (rev + 1, current);
        return Task.FromResult(GetWorkItemsAsync([id], ct).Result[0]);
    }

    public Task AddWorkItemCommentAsync(int id, string html, CancellationToken ct)
    {
        lock (Comments) Comments.Add((id, html));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AzureCommit>> ListCommitsAsync(string repository, DateTimeOffset? since, CancellationToken ct) => Task.FromResult<IReadOnlyList<AzureCommit>>(Commits);

    public Task<IReadOnlyList<AzurePullRequest>> ListPullRequestsAsync(string repository, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AzurePullRequest>>(PullRequests.Where(p => p.Pr.RepositoryName == repository).Select(p => p.Pr).ToList());

    public Task<IReadOnlyList<int>> GetPullRequestWorkItemIdsAsync(string repository, int pullRequestId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<int>>(PullRequests.Where(p => p.Pr.Id == pullRequestId && p.WorkItemId.HasValue).Select(p => p.WorkItemId!.Value).ToList());

    public Task<AzurePullRequest> CreatePullRequestAsync(string repository, string sourceBranch, string targetBranch, string title, string description, int? workItemId, CancellationToken ct)
    {
        lock (PullRequests)
        {
            var pr = new AzurePullRequest(PullRequests.Count + 1, title, "active", $"refs/heads/{sourceBranch}", $"refs/heads/{targetBranch}", "agent",
                DateTimeOffset.UtcNow, null, "abc123", repository);
            PullRequests.Add((pr, workItemId));
            return Task.FromResult(pr);
        }
    }

    public Task<IReadOnlyList<AzureFileChange>> GetPullRequestChangesAsync(string repository, int pullRequestId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AzureFileChange>>(FileContents.Keys.Select(path => new AzureFileChange(path, "edit")).ToList());

    public Task<string?> GetFileContentAsync(string repository, string path, string commitId, CancellationToken ct) =>
        Task.FromResult(FileContents.GetValueOrDefault(path));

    public Task AddPullRequestThreadAsync(string repository, int pullRequestId, string content, string? filePath, int? line, CancellationToken ct)
    {
        lock (Threads) Threads.Add((pullRequestId, content, filePath, line));
        return Task.CompletedTask;
    }

    public string WorkItemWebUrl(int id) => $"https://dev.azure.com/contoso/Shop/_workitems/edit/{id}";
    public string PullRequestWebUrl(string repositoryWebUrl, int pullRequestId) => $"{repositoryWebUrl}/pullrequest/{pullRequestId}";
}
