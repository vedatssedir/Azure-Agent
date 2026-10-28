using AzureAgent.Domain.Common;

namespace AzureAgent.Domain.Agents;

public enum RunStatus
{
    PendingApproval = 0,
    Queued = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    Cancelled = 5,
}

public enum RunKind
{
    Implement = 0,
    Review = 1,
}

/// <summary>One agent job. Token usage and cost accumulate per step for cost-per-job analysis.</summary>
public sealed class AgentRun : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; init; }
    public Guid ProjectId { get; init; }
    public Guid AgentId { get; init; }
    public RunKind Kind { get; init; }
    public RunStatus Status { get; private set; }
    public Guid? WorkItemId { get; init; }
    public int? WorkItemAzureId { get; init; }
    public Guid? PullRequestId { get; init; }
    public string? Branch { get; set; }
    public List<string> PullRequestUrls { get; set; } = [];
    public string? Summary { get; set; }
    public string? Error { get; private set; }
    public string? RequestedBy { get; init; }
    public string? ApprovedBy { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long CachedInputTokens { get; private set; }
    public decimal CostUsd { get; private set; }
    public int ModelCalls { get; private set; }
    public int ToolCalls { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }

    public static AgentRun Create(Guid organizationId, Guid projectId, Guid agentId, RunKind kind, bool requiresApproval,
        Guid? workItemId = null, int? workItemAzureId = null, Guid? pullRequestId = null, string? requestedBy = null) => new()
    {
        OrganizationId = organizationId,
        ProjectId = projectId,
        AgentId = agentId,
        Kind = kind,
        WorkItemId = workItemId,
        WorkItemAzureId = workItemAzureId,
        PullRequestId = pullRequestId,
        RequestedBy = requestedBy,
        Status = requiresApproval ? RunStatus.PendingApproval : RunStatus.Queued,
    };

    public bool IsActive => Status is RunStatus.PendingApproval or RunStatus.Queued or RunStatus.Running;
    public TimeSpan? Duration => StartedAt is { } start && FinishedAt is { } end ? end - start : null;

    public void Approve(string approver)
    {
        if (Status != RunStatus.PendingApproval) throw new DomainException("Only runs waiting for approval can be approved");
        Status = RunStatus.Queued;
        ApprovedBy = approver;
    }

    public void Start(DateTimeOffset now)
    {
        if (Status != RunStatus.Queued) throw new DomainException($"Run cannot start from {Status}");
        Status = RunStatus.Running;
        StartedAt = now;
    }

    public void RecordUsage(long input, long output, long cachedInput, decimal cost)
    {
        InputTokens += input;
        OutputTokens += output;
        CachedInputTokens += cachedInput;
        CostUsd += cost;
        ModelCalls++;
    }

    /// <summary>Usage reported after the fact (a CLI's final totals) that is not a separate model call.</summary>
    public void AddUsage(long input, long output, long cachedInput, decimal cost)
    {
        InputTokens += input;
        OutputTokens += output;
        CachedInputTokens += cachedInput;
        CostUsd += cost;
    }

    public void Succeed(DateTimeOffset now, string? summary)
    {
        if (Status != RunStatus.Running) throw new DomainException($"Run cannot succeed from {Status}");
        Status = RunStatus.Succeeded;
        Summary = summary;
        FinishedAt = now;
    }

    public void Fail(DateTimeOffset now, string error)
    {
        if (!IsActive) return;
        Status = RunStatus.Failed;
        Error = error.Length > 4000 ? error[..4000] : error;
        FinishedAt = now;
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status is not (RunStatus.PendingApproval or RunStatus.Queued)) throw new DomainException("Only queued runs can be cancelled");
        Status = RunStatus.Cancelled;
        FinishedAt = now;
    }
}

public enum RunStepType
{
    Log = 0,
    ModelCall = 1,
    ToolCall = 2,
    Git = 3,
    Error = 4,
}

public sealed class AgentRunStep : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; init; }
    public Guid RunId { get; init; }
    public int Sequence { get; init; }
    public RunStepType Type { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public decimal CostUsd { get; init; }
    public int DurationMs { get; init; }
}

public enum ReviewVerdict
{
    Approve = 0,
    Comment = 1,
    RequestChanges = 2,
}

/// <summary>Result of an AI code review on a pull request.</summary>
public sealed class CodeReview : Entity, IOrganizationScoped
{
    public Guid OrganizationId { get; init; }
    public Guid PullRequestId { get; init; }
    public Guid RunId { get; init; }
    public required string SourceCommit { get; init; }
    public ReviewVerdict Verdict { get; init; }
    public required string Summary { get; init; }
    public List<ReviewFinding> Findings { get; init; } = [];
}

public sealed class ReviewFinding
{
    public required string File { get; init; }
    public int? Line { get; init; }
    public required string Severity { get; init; }
    public required string Message { get; init; }
}
