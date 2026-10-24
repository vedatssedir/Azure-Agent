using AzureAgent.Domain.Common;

namespace AzureAgent.Domain.Agents;

public enum ModelProvider
{
    OpenAI = 0,
    AzureOpenAI = 1,
    Anthropic = 2,
    Gemini = 3,
}

public enum AgentKind
{
    /// <summary>Implements work items: edits code, commits, opens pull requests.</summary>
    Developer = 0,
    /// <summary>Reviews pull requests and posts findings.</summary>
    Reviewer = 1,
}

public sealed class Agent : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; init; }
    public Guid ProjectId { get; init; }
    public required string Name { get; set; }
    public AgentKind Kind { get; set; }
    public ModelProvider Provider { get; set; }
    public required string Model { get; set; }
    public string Instructions { get; set; } = "";
    /// <summary>Work items assigned to this Azure identity are handed to this agent (developer agents).</summary>
    public string? PairedUniqueName { get; set; }
    public Guid? TeamId { get; set; }
    public bool Enabled { get; set; } = true;
    public int MaxConcurrentRuns { get; set; } = 1;
    public decimal? MonthlyBudgetUsd { get; set; }
    public AgentSettings Settings { get; set; } = new();
    public List<AgentSkill> Skills { get; set; } = [];
    public List<AgentMcpServer> McpServers { get; set; } = [];
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

/// <summary>Reusable instructions (Markdown) that can be attached to agents.</summary>
public sealed class Skill : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; init; }
    /// <summary>Lowercase key, unique in the organization.</summary>
    public required string Key { get; init; }
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    public required string Content { get; set; }
}

public sealed class AgentSkill
{
    public Guid AgentId { get; init; }
    public Guid SkillId { get; init; }
}

public enum McpTransport
{
    Stdio = 0,
    Http = 1,
}

public sealed class McpServer : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; init; }
    public required string Name { get; init; }
    public McpTransport Transport { get; set; }
    public string? Command { get; set; }
    public List<string> Arguments { get; set; } = [];
    public string? Url { get; set; }
    /// <summary>Secrets passed to the server as environment variables / headers.</summary>
    public List<string> SecretNames { get; set; } = [];
}

public sealed class AgentMcpServer
{
    public Guid AgentId { get; init; }
    public Guid McpServerId { get; init; }
}
