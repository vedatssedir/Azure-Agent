using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AzureAgent.Application.Abstractions;
using AzureAgent.Domain.Azure;

namespace AzureAgent.Application.Features.Azure;

public static partial class WorkItemMapping
{
    public const string AcceptanceCriteriaField = "Microsoft.VSTS.Common.AcceptanceCriteria";
    public const string PriorityField = "Microsoft.VSTS.Common.Priority";

    private static string Text(IReadOnlyDictionary<string, object?> fields, string name) =>
        fields.TryGetValue(name, out var value) && value is JsonValue json && json.TryGetValue<string>(out var s) ? s : value as string ?? "";

    private static double? Number(IReadOnlyDictionary<string, object?> fields, string name) =>
        fields.TryGetValue(name, out var value) && value is JsonValue json && json.TryGetValue<double>(out var d) ? d : null;

    public static IReadOnlyList<string> ParseTags(string tags) =>
        tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Tags written <c>repo:&lt;name&gt;</c> name the repositories a work item changes.</summary>
    public static List<string> RepositoryNames(IEnumerable<string> tags) =>
        tags.Where(t => t.StartsWith("repo:", StringComparison.OrdinalIgnoreCase)).Select(t => t[5..].Trim())
            .Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static (string? UniqueName, string? DisplayName) AssignedTo(IReadOnlyDictionary<string, object?> fields) => Identity(fields, "System.AssignedTo");

    private static (string? UniqueName, string? DisplayName) Identity(IReadOnlyDictionary<string, object?> fields, string field)
    {
        if (!fields.TryGetValue(field, out var value) || value is not JsonObject identity) return (null, null);
        return (identity["uniqueName"]?.GetValue<string>()?.ToLowerInvariant(), identity["displayName"]?.GetValue<string>());
    }

    public static void Apply(WorkItem target, AzureWorkItem source)
    {
        var f = source.Fields;
        var tags = ParseTags(Text(f, "System.Tags"));
        var (uniqueName, displayName) = AssignedTo(f);
        target.Revision = source.Rev;
        target.Type = Text(f, "System.WorkItemType") is { Length: > 0 } type ? type : "Work Item";
        target.Title = Text(f, "System.Title") is { Length: > 0 } title ? title : $"Work item {source.Id}";
        target.State = Text(f, "System.State") is { Length: > 0 } state ? state : "New";
        target.AssignedToUniqueName = uniqueName;
        target.AssignedToDisplayName = displayName;
        target.BoardColumn = Text(f, "System.BoardColumn") is { Length: > 0 } column ? column : null;
        target.CreatedByDisplayName = Identity(f, "System.CreatedBy").DisplayName;
        target.AzureCreatedAt = DateTimeOffset.TryParse(Text(f, "System.CreatedDate"), out var created) ? created : null;
        target.AreaPath = Text(f, "System.AreaPath");
        target.IterationPath = Text(f, "System.IterationPath");
        target.Priority = Number(f, "Microsoft.VSTS.Common.Priority") is { } p ? (int)p : null;
        target.StoryPoints = Number(f, "Microsoft.VSTS.Scheduling.StoryPoints");
        target.ParentAzureId = Number(f, "System.Parent") is { } parent ? (int)parent : null;
        target.Tags = tags.ToList();
        target.RepositoryNames = RepositoryNames(tags);
        target.Description = Text(f, "System.Description") is { Length: > 0 } description ? description : Text(f, "Microsoft.VSTS.TCM.ReproSteps");
        target.AcceptanceCriteria = Text(f, "Microsoft.VSTS.Common.AcceptanceCriteria");
        target.ChangedAt = DateTimeOffset.TryParse(Text(f, "System.ChangedDate"), out var changed) ? changed : DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Reconciliation query: on the first sync every item that is open or changed in the last year;
    /// afterwards everything changed since the last sync (a day of overlap).
    /// </summary>
    public static string ReconciliationQuery(DateTimeOffset? since, IReadOnlyCollection<string> closedStates)
    {
        var open = closedStates.Count == 0 ? "" : $" OR [System.State] NOT IN ({string.Join(", ", closedStates.Select(st => $"'{st.Replace("'", "''")}'"))})";
        var filter = since is { } s
            ? $"[System.ChangedDate] >= '{s.UtcDateTime.AddDays(-1):yyyy-MM-dd}'"
            : $"([System.ChangedDate] >= @today - 365{open})";
        return $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND {filter} ORDER BY [System.ChangedDate] DESC";
    }

    /// <summary>The agent a work item asks for with an <c>agent:&lt;slug&gt;</c> tag, if any.</summary>
    public static string? AgentTag(IEnumerable<string> tags) =>
        tags.FirstOrDefault(t => t.StartsWith("agent:", StringComparison.OrdinalIgnoreCase))?[6..].Trim().ToLowerInvariant() is { Length: > 0 } slug ? slug : null;

    public static List<int> WorkItemReferences(string message) =>
        WorkItemReference().Matches(message).Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToList();

    public static string HtmlToText(string html)
    {
        var text = BlockEnd().Replace(html, "\n");
        text = Tag().Replace(text, "");
        text = System.Net.WebUtility.HtmlDecode(text);
        return MultipleBlankLines().Replace(text, "\n\n").Trim();
    }

    public static string BranchName(int azureId, string title)
    {
        var slug = Common.Slugs.From(title, 40);
        return $"ab/{azureId}-{slug}";
    }

    [GeneratedRegex(@"AB#(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex WorkItemReference();

    [GeneratedRegex(@"<br\s*/?>|</(p|div|li|h[1-6]|tr)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultipleBlankLines();
}
