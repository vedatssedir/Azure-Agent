using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using Microsoft.Extensions.Logging;

namespace AzureAgent.Infrastructure.AzureDevOps;

public sealed class AzureDevOpsException(HttpStatusCode status, string method, string path)
    : AzureRequestException((int)status, $"Azure DevOps {method} {path} failed with HTTP {(int)status}")
{
    public HttpStatusCode HttpStatus { get; } = status;
}

public sealed class AzureDevOpsClientFactory(IHttpClientFactory httpClients, ILoggerFactory loggers) : IAzureDevOpsClientFactory
{
    public const string HttpClientName = "azure-devops";

    public IAzureDevOpsClient Create(AzureConnectionInfo connection) =>
        new AzureDevOpsClient(httpClients.CreateClient(HttpClientName), connection, loggers.CreateLogger<AzureDevOpsClient>());
}

/// <summary>
/// Azure DevOps REST client. Authenticates with a PAT (HTTP Basic) or an Entra ID token (Bearer), retries 429/503 honoring
/// Retry-After, and never includes response bodies in errors (they can echo request data).
/// </summary>
public sealed class AzureDevOpsClient(HttpClient http, AzureConnectionInfo connection, ILogger<AzureDevOpsClient> logger) : IAzureDevOpsClient
{
    private const int MaxAttempts = 4;
    private static readonly string[] WorkItemFields =
    [
        "System.Title", "System.State", "System.WorkItemType", "System.AssignedTo", "System.Tags", "System.AreaPath",
        "System.IterationPath", "System.Description", "System.ChangedDate", "System.Parent", "Microsoft.VSTS.Common.Priority",
        "Microsoft.VSTS.Common.AcceptanceCriteria", "Microsoft.VSTS.Scheduling.StoryPoints", "Microsoft.VSTS.TCM.ReproSteps",
        "System.BoardColumn", "System.CreatedBy", "System.CreatedDate",
    ];

    private readonly string _base = connection.OrganizationUrl.TrimEnd('/');
    private readonly string _project = Uri.EscapeDataString(connection.Project);
    private readonly AuthenticationHeaderValue _auth = AuthenticationHeaderValue.Parse(connection.Credential.HeaderValue);

    public static TimeSpan RetryDelay(int attempt, RetryConditionHeaderValue? retryAfter)
    {
        var delay = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt)));
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay;
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, string apiVersion = "7.1",
        string contentType = "application/json")
    {
        var url = $"{_base}{path}{(path.Contains('?') ? '&' : '?')}api-version={apiVersion}";
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = _auth;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            // Without this a rejected credential gets a 302 to the interactive sign-in page instead of a 401.
            request.Headers.Add("X-TFS-FedAuthRedirect", "Suppress");
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, contentType);
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt + 1 < MaxAttempts)
            {
                var delay = RetryDelay(attempt, response.Headers.RetryAfter);
                logger.LogWarning("Azure DevOps throttled {Method} {Path}; retrying in {Delay}", method, path.Split('?')[0], delay);
                await Task.Delay(delay, ct);
                continue;
            }
            // 203 and redirects are the sign-in page Azure returns for a rejected credential.
            if (!response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NonAuthoritativeInformation)
                throw new AzureDevOpsException(response.StatusCode, method.Method, path.Split('?')[0]);
            var text = await response.Content.ReadAsStringAsync(ct);
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
    }

    private string RepoPath(string repository) => $"/{_project}/_apis/git/repositories/{Uri.EscapeDataString(repository)}";

    private static JsonArray Values(JsonNode? node) => node?["value"]?.AsArray() ?? [];

    private static string Str(JsonNode? node) => node?.GetValue<string>() ?? "";

    public async Task<string> GetProjectNameAsync(CancellationToken ct) =>
        Str((await SendAsync(HttpMethod.Get, $"/_apis/projects/{_project}", null, ct))?["name"]);

    public async Task<IReadOnlyList<AzureProject>> ListProjectsAsync(CancellationToken ct) =>
        Values(await SendAsync(HttpMethod.Get, "/_apis/projects?$top=500&stateFilter=wellFormed", null, ct))
            .Select(p => new AzureProject(Str(p!["id"]), Str(p["name"]), p["description"]?.GetValue<string>(), Str(p["state"]))).ToList();

    public async Task<IReadOnlyList<AzureBoard>> ListBoardsAsync(string teamId, CancellationToken ct)
    {
        var team = Uri.EscapeDataString(teamId);
        var boards = new List<AzureBoard>();
        foreach (var reference in Values(await SendAsync(HttpMethod.Get, $"/{_project}/{team}/_apis/work/boards", null, ct)))
        {
            var board = await SendAsync(HttpMethod.Get, $"/{_project}/{team}/_apis/work/boards/{Uri.EscapeDataString(Str(reference!["id"]))}", null, ct);
            var columns = (board?["columns"]?.AsArray() ?? []).Select(c => new AzureBoardColumn(Str(c!["name"]), Str(c["columnType"]),
                c["itemLimit"]?.GetValue<int>() ?? 0,
                (c["stateMappings"]?.AsObject() ?? []).ToDictionary(m => m.Key, m => m.Value?.GetValue<string>() ?? ""))).ToList();
            boards.Add(new AzureBoard(Str(board?["name"]), board?["fields"]?["columnField"]?["referenceName"]?.GetValue<string>(), columns));
        }
        return boards;
    }

    public async Task<IReadOnlyList<AzureIteration>> ListIterationsAsync(string teamId, CancellationToken ct) =>
        Values(await SendAsync(HttpMethod.Get, $"/{_project}/{Uri.EscapeDataString(teamId)}/_apis/work/teamsettings/iterations", null, ct))
            .Select(i => new AzureIteration(Str(i!["id"]), Str(i["name"]), Str(i["path"]),
                i["attributes"]?["startDate"]?.GetValue<string>() is { } start ? DateTimeOffset.Parse(start) : null,
                i["attributes"]?["finishDate"]?.GetValue<string>() is { } finish ? DateTimeOffset.Parse(finish) : null,
                i["attributes"]?["timeFrame"]?.GetValue<string>() ?? "")).ToList();

    public async Task<IReadOnlyList<AzureComment>> ListWorkItemCommentsAsync(int id, CancellationToken ct) =>
        ((await SendAsync(HttpMethod.Get, $"/{_project}/_apis/wit/workItems/{id}/comments?$top=200&order=asc", null, ct, apiVersion: "7.1-preview.4"))?["comments"]?.AsArray() ?? [])
            .Select(c => new AzureComment(c!["id"]!.GetValue<int>(), Str(c["text"]), Str(c["createdBy"]?["displayName"]),
                Str(c["createdBy"]?["uniqueName"]), DateTimeOffset.Parse(Str(c["createdDate"])))).ToList();

    public async Task<IReadOnlyList<AzureTeam>> ListTeamsAsync(CancellationToken ct) =>
        Values(await SendAsync(HttpMethod.Get, $"/_apis/projects/{_project}/teams?$top=500", null, ct))
            .Select(t => new AzureTeam(Str(t!["id"]), Str(t["name"]))).ToList();

    public async Task<IReadOnlyList<AzureIdentity>> ListTeamMembersAsync(string teamId, CancellationToken ct) =>
        Values(await SendAsync(HttpMethod.Get, $"/_apis/projects/{_project}/teams/{Uri.EscapeDataString(teamId)}/members?$top=500", null, ct))
            .Select(m => new AzureIdentity(Str(m!["identity"]?["displayName"]), Str(m["identity"]?["uniqueName"]))).ToList();

    public async Task<IReadOnlyList<(string Path, bool IncludeChildren)>> ListTeamAreasAsync(string teamId, CancellationToken ct) =>
        ((await SendAsync(HttpMethod.Get, $"/{_project}/{Uri.EscapeDataString(teamId)}/_apis/work/teamsettings/teamfieldvalues", null, ct))?["values"]?.AsArray() ?? [])
            .Select(v => (Str(v!["value"]), v["includeChildren"]?.GetValue<bool>() == true)).ToList();

    public async Task<AzureIdentity> GetAuthenticatedIdentityAsync(CancellationToken ct)
    {
        var user = (await SendAsync(HttpMethod.Get, "/_apis/connectionData", null, ct, apiVersion: "7.1-preview.1"))?["authenticatedUser"];
        var account = user?["properties"]?["Account"]?["$value"]?.GetValue<string>();
        return new AzureIdentity(Str(user?["providerDisplayName"]), account ?? "");
    }

    public async Task<IReadOnlyList<AzureWorkItemType>> ListWorkItemTypesAsync(CancellationToken ct) =>
        Values(await SendAsync(HttpMethod.Get, $"/{_project}/_apis/wit/workitemtypes", null, ct))
            .Select(t => new AzureWorkItemType(Str(t!["name"]), Str(t["color"]), t["isDisabled"]?.GetValue<bool>() == true,
                (t["states"]?.AsArray() ?? []).Select(st => new AzureWorkItemState(Str(st!["name"]), Str(st["color"]), Str(st["category"]))).ToList(),
                (t["fields"]?.AsArray() ?? []).Select(f => Str(f!["referenceName"])).ToList())).ToList();

    public async Task<AzureWorkItem> CreateWorkItemAsync(string type, IReadOnlyDictionary<string, object?> fields, CancellationToken ct)
    {
        var patch = fields.Where(f => f.Value is not null).Select(f => new { op = "add", path = $"/fields/{f.Key}", value = f.Value }).ToArray();
        return ToWorkItem(await SendAsync(HttpMethod.Post, $"/{_project}/_apis/wit/workitems/${Uri.EscapeDataString(type)}", patch, ct,
            contentType: "application/json-patch+json"));
    }

    public async Task<IReadOnlyList<AzureRepository>> ListRepositoriesAsync(CancellationToken ct) =>
        Values(await SendAsync(HttpMethod.Get, $"/{_project}/_apis/git/repositories", null, ct))
            .Select(r => new AzureRepository(Str(r!["id"]), Str(r["name"]), r["defaultBranch"]?.GetValue<string>()?.Replace("refs/heads/", ""),
                Str(r["remoteUrl"]), Str(r["webUrl"]), r["size"]?.GetValue<long>() ?? 0, r["isDisabled"]?.GetValue<bool>() == true)).ToList();

    public async Task<IReadOnlyList<string>> ListRootFilesAsync(string repository, CancellationToken ct) =>
        Values(await SendAsync(HttpMethod.Get, $"{RepoPath(repository)}/items?scopePath=%2F&recursionLevel=OneLevel", null, ct))
            .Where(i => i!["isFolder"]?.GetValue<bool>() != true).Select(i => Str(i!["path"]).TrimStart('/')).ToList();

    public async Task<IReadOnlyList<int>> QueryWorkItemIdsAsync(string wiql, CancellationToken ct) =>
        ((await SendAsync(HttpMethod.Post, $"/{_project}/_apis/wit/wiql?$top=5000", new { query = wiql }, ct))?["workItems"]?.AsArray() ?? [])
            .Select(w => w!["id"]!.GetValue<int>()).ToList();

    public async Task<IReadOnlyList<AzureWorkItem>> GetWorkItemsAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        var items = new List<AzureWorkItem>();
        foreach (var chunk in ids.Chunk(200))
        {
            var result = await SendAsync(HttpMethod.Post, $"/{_project}/_apis/wit/workitemsbatch", new { ids = chunk, fields = WorkItemFields }, ct);
            items.AddRange(Values(result).Select(ToWorkItem));
        }
        return items;
    }

    private static AzureWorkItem ToWorkItem(JsonNode? node)
    {
        var fields = node!["fields"]!.AsObject().ToDictionary(p => p.Key, p => (object?)p.Value?.DeepClone());
        return new AzureWorkItem(node["id"]!.GetValue<int>(), node["rev"]!.GetValue<int>(), fields);
    }

    public async Task<AzureWorkItem> UpdateWorkItemFieldsAsync(int id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct)
    {
        var patch = fields.Select(f => new { op = "add", path = $"/fields/{f.Key}", value = f.Value }).ToArray();
        return ToWorkItem(await SendAsync(HttpMethod.Patch, $"/{_project}/_apis/wit/workitems/{id}", patch, ct, contentType: "application/json-patch+json"));
    }

    public Task AddWorkItemCommentAsync(int id, string html, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"/{_project}/_apis/wit/workItems/{id}/comments", new { text = html }, ct, apiVersion: "7.1-preview.4");

    private static AzurePullRequest ToPullRequest(JsonNode? p) => new(
        p!["pullRequestId"]!.GetValue<int>(), Str(p["title"]), Str(p["status"]), Str(p["sourceRefName"]), Str(p["targetRefName"]),
        Str(p["createdBy"]?["displayName"]), DateTimeOffset.Parse(Str(p["creationDate"])),
        p["closedDate"]?.GetValue<string>() is { } closed ? DateTimeOffset.Parse(closed) : null,
        p["lastMergeSourceCommit"]?["commitId"]?.GetValue<string>(), Str(p["repository"]?["name"]));

    public async Task<IReadOnlyList<AzurePullRequest>> ListPullRequestsAsync(string repository, CancellationToken ct) =>
        Values(await SendAsync(HttpMethod.Get, $"{RepoPath(repository)}/pullrequests?searchCriteria.status=all&$top=300", null, ct))
            .Select(ToPullRequest).ToList();

    public async Task<IReadOnlyList<int>> GetPullRequestWorkItemIdsAsync(string repository, int pullRequestId, CancellationToken ct) =>
        Values(await SendAsync(HttpMethod.Get, $"{RepoPath(repository)}/pullRequests/{pullRequestId}/workitems", null, ct))
            .Select(w => int.TryParse(Str(w!["id"]), out var id) ? id : 0).Where(id => id > 0).ToList();

    public async Task<AzurePullRequest> CreatePullRequestAsync(string repository, string sourceBranch, string targetBranch, string title,
        string description, int? workItemId, CancellationToken ct)
    {
        static string Ref(string branch) => branch.StartsWith("refs/", StringComparison.Ordinal) ? branch : $"refs/heads/{branch}";
        var body = new
        {
            sourceRefName = Ref(sourceBranch),
            targetRefName = Ref(targetBranch),
            title,
            description = description.Length > 4000 ? description[..4000] : description,
            workItemRefs = workItemId is { } id ? new[] { new { id = id.ToString() } } : [],
        };
        return ToPullRequest(await SendAsync(HttpMethod.Post, $"{RepoPath(repository)}/pullrequests", body, ct));
    }

    public async Task<IReadOnlyList<AzureFileChange>> GetPullRequestChangesAsync(string repository, int pullRequestId, CancellationToken ct)
    {
        var iterations = Values(await SendAsync(HttpMethod.Get, $"{RepoPath(repository)}/pullRequests/{pullRequestId}/iterations", null, ct));
        if (iterations.Count == 0) return [];
        var last = iterations[^1]!["id"]!.GetValue<int>();
        var changes = await SendAsync(HttpMethod.Get, $"{RepoPath(repository)}/pullRequests/{pullRequestId}/iterations/{last}/changes?$top=1000", null, ct);
        return (changes?["changeEntries"]?.AsArray() ?? [])
            .Where(c => c!["item"]?["isFolder"]?.GetValue<bool>() != true)
            .Select(c => new AzureFileChange(Str(c!["item"]?["path"]), Str(c["changeType"]))).ToList();
    }

    public async Task<string?> GetFileContentAsync(string repository, string path, string commitId, CancellationToken ct)
    {
        try
        {
            var node = await SendAsync(HttpMethod.Get,
                $"{RepoPath(repository)}/items?path={Uri.EscapeDataString(path)}&versionDescriptor.version={commitId}&versionDescriptor.versionType=commit&includeContent=true",
                null, ct);
            return node?["content"]?.GetValue<string>();
        }
        catch (AzureDevOpsException e) when (e.HttpStatus == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task AddPullRequestThreadAsync(string repository, int pullRequestId, string content, string? filePath, int? line, CancellationToken ct)
    {
        object body = filePath is null
            ? new { comments = new[] { new { parentCommentId = 0, content, commentType = 1 } }, status = 1 }
            : new
            {
                comments = new[] { new { parentCommentId = 0, content, commentType = 1 } },
                status = 1,
                threadContext = new
                {
                    filePath,
                    rightFileStart = new { line = line ?? 1, offset = 1 },
                    rightFileEnd = new { line = line ?? 1, offset = 1 },
                },
            };
        return SendAsync(HttpMethod.Post, $"{RepoPath(repository)}/pullRequests/{pullRequestId}/threads", body, ct);
    }

    public string WorkItemWebUrl(int id) => $"{_base}/{_project}/_workitems/edit/{id}";

    public string PullRequestWebUrl(string repositoryWebUrl, int pullRequestId) => $"{repositoryWebUrl}/pullrequest/{pullRequestId}";
}
