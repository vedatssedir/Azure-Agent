using AzureAgent.Domain.Common;

namespace AzureAgent.Domain.Agents;

public enum ModelProvider
{
    OpenAI = 0,
    AzureOpenAI = 1,
    Anthropic = 2,
    Gemini = 3,
}

/// <summary>What drives the agent's work loop.</summary>
public enum AgentRuntime
{
    /// <summary>Microsoft Agent Framework with the platform's workspace tools (any provider).</summary>
    Framework = 0,
    /// <summary>Claude Code CLI (<c>claude -p</c>) in the run's workspace; Anthropic models.</summary>
    ClaudeCode = 1,
    /// <summary>Cursor CLI (<c>cursor-agent -p</c>) in the run's workspace; Cursor's models.</summary>
    CursorCli = 2,
}

public enum AgentKind
{
    /// <summary>Implements work items: edits code, commits, opens pull requests.</summary>
    Developer = 0,
    /// <summary>Reviews pull requests and posts findings.</summary>
    Reviewer = 1,
}

/// <summary>
/// An agent definition (like a Paperclip agent): who owns it, what drives it (runtime, model), how it
/// behaves (instructions) and what it knows (its own skills). A developer can own several agents;
/// one of them is their default for work assigned to them. Items tagged <c>agent:&lt;slug&gt;</c> go
/// to that agent directly.
/// </summary>
public sealed class Agent : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; init; }
    /// <summary>Developer the agent works for (its permissions apply). Null = shared agent managed by admins (e.g. the reviewer).</summary>
    public Guid? OwnerUserId { get; set; }
    /// <summary>Limit the agent to one project; null = every project its owner can work on.</summary>
    public Guid? ProjectId { get; set; }
    public required string Name { get; set; }
    /// <summary>Lowercase identifier used in work item tags (agent:&lt;slug&gt;), unique in the organization.</summary>
    public required string Slug { get; set; }
    /// <summary>Short role description shown on the agent card (e.g. "Backend developer").</summary>
    public string? Title { get; set; }
    public AgentKind Kind { get; set; }
    public ModelProvider Provider { get; set; }
    public AgentRuntime Runtime { get; set; }
    public required string Model { get; set; }
    public string Instructions { get; set; } = "";
    /// <summary>The owner's default agent: work assigned to the owner starts here.</summary>
    public bool IsDefault { get; set; }
    public bool Enabled { get; set; } = true;
    public int MaxConcurrentRuns { get; set; } = 1;
    public decimal? MonthlyBudgetUsd { get; set; }
    public AgentSettings Settings { get; set; } = new();
    /// <summary>The agent's own skills (Markdown), added to its instructions on every run.</summary>
    public List<AgentSkill> Skills { get; set; } = [];
}

public sealed class AgentSkill
{
    public required string Name { get; set; }
    public required string Content { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class AgentSettings
{
    public float? Temperature { get; set; }
    /// <summary>Upper bound on model/tool round trips in one run.</summary>
    public int MaxIterations { get; set; } = 40;
    /// <summary>Stop the run when its token usage exceeds this many tokens.</summary>
    public int MaxTokensPerRun { get; set; } = 400_000;
    /// <summary>Stop the run when its cost exceeds this amount (USD).</summary>
    public decimal MaxCostPerRunUsd { get; set; } = 5m;
    public int TimeoutMinutes { get; set; } = 45;
    /// <summary>Azure OpenAI endpoint, or another OpenAI-compatible base URL.</summary>
    public string? Endpoint { get; set; }
}
