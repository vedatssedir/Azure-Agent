using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using AzureAgent.Application.Abstractions;

namespace AzureAgent.IntegrationTests.Fakes;

/// <summary>
/// Fake Azure DevOps organization: in-memory projects (each isolated), plus who every token
/// authenticates as. Organization-level clients (empty project) list the projects.
/// </summary>
public sealed class FakeAzureDevOpsFactory : IAzureDevOpsClientFactory
{
    private readonly ConcurrentDictionary<string, FakeAzureDevOps> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly FakeAzureDevOps _organization;

    public FakeAzureDevOpsFactory() => _organization = new FakeAzureDevOps(this, "", "");

    /// <summary>Who each credential token authenticates as (connectionData).</summary>
    public ConcurrentDictionary<string, AzureIdentity> TokenIdentities { get; } = new();
    /// <summary>When set, only these tokens are accepted.</summary>
    public HashSet<string>? AcceptedTokens { get; set; }
    /// <summary>Tokens that can read but whose work item writes get HTTP 401 (expired, or missing the write scope).</summary>
    public HashSet<string> ReadOnlyTokens { get; } = [];

    public FakeAzureDevOps Project(string name) => _projects.GetOrAdd(name, n => new FakeAzureDevOps(this, $"azure-{n.ToLowerInvariant()}", n));

    public void RemoveProject(string name) => _projects.TryRemove(name, out _);

    public IReadOnlyList<AzureProject> Projects => _projects.Values.Select(p => new AzureProject(p.AzureId, p.Name, $"{p.Name} project", "wellFormed")).ToList();

    public IAzureDevOpsClient Create(AzureConnectionInfo connection) =>
        (string.IsNullOrEmpty(connection.Project) ? _organization : Project(connection.Project)).Create(connection);
}

/// <summary>Binds calls on the shared fake project to the credential of the client that made them.</summary>
public class CredentialBoundClient : DispatchProxy
{
    internal FakeAzureDevOps Target { get; set; } = null!;
    internal AzureCredential Credential { get; set; } = null!;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        FakeAzureDevOps.CurrentCredential.Value = Credential;
        try { return method!.Invoke(Target, args); }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(e.InnerException);
            throw;
        }
    }
}

/// <summary>In-memory Azure DevOps project. Repositories point at local bare git repositories.</summary>
public sealed class FakeAzureDevOps(FakeAzureDevOpsFactory organization, string azureId, string name) : IAzureDevOpsClient
{
    public string AzureId { get; } = azureId;
    public string Name { get; } = name;
    public ConcurrentDictionary<int, (int Rev, Dictionary<string, object?> Fields)> WorkItems { get; } = new();
    public List<AzureTeam> Teams { get; } = [new($"{azureId}-team", $"{name} Team")];
    public List<AzureIdentity> Members { get; } = [new("Jane Doe", "Jane@Contoso.com"), new("Joe Smith", "joe@contoso.com"), new("Admin", "admin@contoso.com")];
    public List<AzureRepository> Repositories { get; } = [];
    public List<(AzurePullRequest Pr, int? WorkItemId)> PullRequests { get; } = [];
    public List<(int WorkItemId, string Html, string Owner)> Comments { get; } = [];
    public List<(int PullRequestId, string Content, string? File, int? Line)> Threads { get; } = [];
    public Dictionary<string, string> FileContents { get; } = [];
    public Dictionary<string, List<AzureIdentity>> TeamMembers { get; } = [];
    public Dictionary<string, List<(string Path, bool IncludeChildren)>> TeamAreas { get; } = [];
    /// <summary>Every write with the credential owner that made it.</summary>
    public ConcurrentQueue<(string Operation, int WorkItemId, string Owner, string Scheme)> Writes { get; } = new();

    public List<AzureWorkItemType> WorkItemTypes { get; } =
    [
        new("Issue", "339947", false,
            [new("To Do", "b2b2b2", "Proposed"), new("Doing", "007acc", "InProgress"), new("Done", "339933", "Completed")],
            ["System.Title", "System.Description", "Microsoft.VSTS.Common.Priority"]),
        new("Epic", "ff7b00", false, [new("To Do", "b2b2b2", "Proposed"), new("Doing", "007acc", "InProgress"), new("Done", "339933", "Completed")], ["System.Title"]),
        new("User Story", "009ccc", false,
            [new("New", "b2b2b2", "Proposed"), new("Active", "007acc", "InProgress"), new("Resolved", "ff9d00", "Resolved"), new("Closed", "339933", "Completed")],
            ["System.Title", "System.Description", "Microsoft.VSTS.Common.AcceptanceCriteria", "Microsoft.VSTS.Common.Priority"]),
    ];

    /// <summary>Kanban boards of every team: Issues (To Do / Doing (WIP 5) / Done) and Epics.</summary>
    public List<AzureBoard> Boards { get; } =
    [
        new("Issues", "WEF_ISSUES_Kanban.Column",
        [
            new("To Do", "incoming", 0, new Dictionary<string, string> { ["Issue"] = "To Do", ["User Story"] = "New" }),
            new("Doing", "inProgress", 5, new Dictionary<string, string> { ["Issue"] = "Doing", ["User Story"] = "Active" }),
            new("Review", "inProgress", 0, new Dictionary<string, string> { ["Issue"] = "Doing", ["User Story"] = "Resolved" }),
            new("Done", "outgoing", 0, new Dictionary<string, string> { ["Issue"] = "Done", ["User Story"] = "Closed" }),
        ]),
        new("Epics", null, [new("To Do", "incoming", 0, new Dictionary<string, string> { ["Epic"] = "To Do" }), new("Done", "outgoing", 0, new Dictionary<string, string> { ["Epic"] = "Done" })]),
    ];

    public List<AzureIteration> Iterations { get; } =
    [
        new("it-1", "Sprint 1", $"{name}\\Sprint 1", DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow.AddDays(7), "current"),
        new("it-2", "Sprint 2", $"{name}\\Sprint 2", DateTimeOffset.UtcNow.AddDays(7), DateTimeOffset.UtcNow.AddDays(21), "future"),
    ];

    internal static readonly AsyncLocal<AzureCredential?> CurrentCredential = new();

    public IAzureDevOpsClient Create(AzureConnectionInfo connection)
    {
        if (organization.AcceptedTokens is { } accepted && !accepted.Contains(connection.Credential.Token))
            throw new InvalidOperationException("Azure DevOps rejected the credential (HTTP 401)");
        var client = DispatchProxy.Create<IAzureDevOpsClient, CredentialBoundClient>();
        var bound = (CredentialBoundClient)(object)client;
        bound.Target = this;
        bound.Credential = connection.Credential;
        return client;
    }

    private string Owner => CurrentCredential.Value?.Owner ?? "?";

    private void RecordWrite(string operation, int id) => Writes.Enqueue((operation, id, Owner, CurrentCredential.Value?.Scheme ?? "?"));

    public void AddWorkItem(int id, string title, string state, string? assignee = null, string tags = "", string? areaPath = null, string type = "Issue",
        string? iteration = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["System.Title"] = JsonValue.Create(title),
            ["System.State"] = JsonValue.Create(state),
            ["System.WorkItemType"] = JsonValue.Create(type),
            ["System.Tags"] = JsonValue.Create(tags),
            ["System.Description"] = JsonValue.Create($"<p>{title} – details</p>"),
            ["System.ChangedDate"] = JsonValue.Create(DateTimeOffset.UtcNow.ToString("O")),
            ["System.CreatedDate"] = JsonValue.Create(DateTimeOffset.UtcNow.AddDays(-2).ToString("O")),
            ["System.CreatedBy"] = Identity("admin@contoso.com"),
            ["System.AreaPath"] = JsonValue.Create(areaPath ?? Name),
            ["System.IterationPath"] = JsonValue.Create(iteration ?? $"{Name}\\Sprint 1"),
        };
        if (assignee is not null) fields["System.AssignedTo"] = Identity(assignee);
        WorkItems[id] = (1, fields);
    }

    public string Field(int id, string name) => WorkItems[id].Fields.GetValueOrDefault(name)?.ToString() ?? "";

    private JsonObject Identity(string uniqueName)
    {
        var member = Members.FirstOrDefault(m => m.UniqueName.Equals(uniqueName, StringComparison.OrdinalIgnoreCase)) ?? new AzureIdentity(uniqueName, uniqueName);
        return new JsonObject { ["displayName"] = member.DisplayName, ["uniqueName"] = member.UniqueName };
    }

    public Task<string> GetProjectNameAsync(CancellationToken ct) => Task.FromResult(Name);
    public Task<IReadOnlyList<AzureProject>> ListProjectsAsync(CancellationToken ct) => Task.FromResult(organization.Projects);
    public Task<IReadOnlyList<AzureBoard>> ListBoardsAsync(string teamId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AzureBoard>>(Boards);
    public Task<IReadOnlyList<AzureIteration>> ListIterationsAsync(string teamId, CancellationToken ct) => Task.FromResult<IReadOnlyList<AzureIteration>>(Iterations);

    public Task<IReadOnlyList<AzureComment>> ListWorkItemCommentsAsync(int id, CancellationToken ct)
    {
        lock (Comments)
            return Task.FromResult<IReadOnlyList<AzureComment>>(Comments.Where(c => c.WorkItemId == id)
                .Select((c, i) => new AzureComment(i + 1, c.Html, c.Owner, c.Owner, DateTimeOffset.UtcNow)).Reverse().ToList());
    }

    public Task<IReadOnlyList<AzureTeam>> ListTeamsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AzureTeam>>(Teams);
    public Task<IReadOnlyList<AzureIdentity>> ListTeamMembersAsync(string teamId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AzureIdentity>>(TeamMembers.GetValueOrDefault(teamId) ?? Members);
    public Task<IReadOnlyList<(string Path, bool IncludeChildren)>> ListTeamAreasAsync(string teamId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(string, bool)>>(TeamAreas.GetValueOrDefault(teamId) ?? [(Name, true)]);

    public Task<AzureIdentity> GetAuthenticatedIdentityAsync(CancellationToken ct) =>
        CurrentCredential.Value is { } c && organization.TokenIdentities.TryGetValue(c.Token, out var identity)
            ? Task.FromResult(identity)
            : throw new InvalidOperationException("Azure DevOps GET /_apis/connectionData failed with HTTP 401");

    public Task<IReadOnlyList<AzureWorkItemType>> ListWorkItemTypesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AzureWorkItemType>>(WorkItemTypes);

    public Task<AzureWorkItem> CreateWorkItemAsync(string type, IReadOnlyDictionary<string, object?> fields, CancellationToken ct)
    {
        int id;
        lock (WorkItems)
        {
            id = WorkItems.IsEmpty ? 1000 : WorkItems.Keys.Max() + 1;
            var values = new Dictionary<string, object?>
            {
                ["System.WorkItemType"] = JsonValue.Create(type),
                ["System.State"] = JsonValue.Create(WorkItemTypes.First(t => t.Name == type).States[0].Name),
                ["System.ChangedDate"] = JsonValue.Create(DateTimeOffset.UtcNow.ToString("O")),
                ["System.AreaPath"] = JsonValue.Create(Name),
            };
            foreach (var (key, value) in fields)
                values[key] = key == "System.AssignedTo" ? (value is string s && s.Length > 0 ? Identity(s) : null) : JsonValue.Create(value?.ToString());
            WorkItems[id] = (1, values);
        }
        RecordWrite("create", id);
        return Task.FromResult(GetWorkItemsAsync([id], ct).Result[0]);
    }

    public Task<IReadOnlyList<AzureRepository>> ListRepositoriesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AzureRepository>>(Repositories);
    public Task<IReadOnlyList<string>> ListRootFilesAsync(string repository, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["README.md"]);
    public Task<IReadOnlyList<int>> QueryWorkItemIdsAsync(string wiql, CancellationToken ct) => Task.FromResult<IReadOnlyList<int>>(WorkItems.Keys.ToList());

    public Task<IReadOnlyList<AzureWorkItem>> GetWorkItemsAsync(IReadOnlyCollection<int> ids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AzureWorkItem>>(ids.Where(WorkItems.ContainsKey)
            .Select(id => new AzureWorkItem(id, WorkItems[id].Rev, WorkItems[id].Fields.ToDictionary(f => f.Key, f => f.Value is JsonNode n ? (object?)n.DeepClone() : f.Value))).ToList());

    public Task<AzureWorkItem> UpdateWorkItemFieldsAsync(int id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct)
    {
        if (CurrentCredential.Value is { } credential && organization.ReadOnlyTokens.Contains(credential.Token))
            throw new AzureAgent.Application.Common.AzureRequestException(401, $"Azure DevOps PATCH /_apis/wit/workitems/{id} failed with HTTP 401");
        var (rev, current) = WorkItems[id];
        foreach (var (key, value) in fields)
            current[key] = key == "System.AssignedTo" ? (value is string s && s.Length > 0 ? Identity(s) : null) : JsonValue.Create(value?.ToString());
        // Like Azure: the Kanban column field drives System.BoardColumn; a state change lands in the first column mapping it.
        if (fields.Keys.FirstOrDefault(k => k.EndsWith("Kanban.Column", StringComparison.Ordinal)) is { } columnField)
            current["System.BoardColumn"] = JsonValue.Create(fields[columnField]?.ToString());
        else if (fields.ContainsKey("System.State"))
            current["System.BoardColumn"] = JsonValue.Create(Boards[0].Columns.FirstOrDefault(c => c.StateMappings.TryGetValue(Field(id, "System.WorkItemType"), out var st)
                && st == fields["System.State"]?.ToString())?.Name);
        current["System.ChangedDate"] = JsonValue.Create(DateTimeOffset.UtcNow.ToString("O"));
        WorkItems[id] = (rev + 1, current);
        RecordWrite(fields.ContainsKey("System.AssignedTo") ? "assign" : "update", id);
        return Task.FromResult(GetWorkItemsAsync([id], ct).Result[0]);
    }

    public Task AddWorkItemCommentAsync(int id, string html, CancellationToken ct)
    {
        lock (Comments) Comments.Add((id, html, Owner));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AzurePullRequest>> ListPullRequestsAsync(string repository, CancellationToken ct)
    {
        lock (PullRequests) return Task.FromResult<IReadOnlyList<AzurePullRequest>>(PullRequests.Where(p => p.Pr.RepositoryName == repository).Select(p => p.Pr).ToList());
    }

    public Task<IReadOnlyList<int>> GetPullRequestWorkItemIdsAsync(string repository, int pullRequestId, CancellationToken ct)
    {
        lock (PullRequests)
            return Task.FromResult<IReadOnlyList<int>>(PullRequests.Where(p => p.Pr.Id == pullRequestId && p.WorkItemId.HasValue).Select(p => p.WorkItemId!.Value).ToList());
    }

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

    public Task<string?> GetFileContentAsync(string repository, string path, string commitId, CancellationToken ct) => Task.FromResult(FileContents.GetValueOrDefault(path));

    public Task AddPullRequestThreadAsync(string repository, int pullRequestId, string content, string? filePath, int? line, CancellationToken ct)
    {
        lock (Threads) Threads.Add((pullRequestId, content, filePath, line));
        return Task.CompletedTask;
    }

    public string WorkItemWebUrl(int id) => $"https://dev.azure.com/contoso/{Name}/_workitems/edit/{id}";
    public string PullRequestWebUrl(string repositoryWebUrl, int pullRequestId) => $"{repositoryWebUrl}/pullrequest/{pullRequestId}";
}
