using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Features.Agents;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Application.Features.Costs;
using AzureAgent.Application.Features.Runs;
using AzureAgent.Domain.Agents;
using Carter;
using Wolverine;

namespace AzureAgent.Api.Modules;

/// <summary>Agent definitions, their runs and what they cost.</summary>
public sealed class AgentsModule : ICarterModule
{
    public sealed record SaveAgentRequest(string Name, string? Title, AgentKind Kind, AgentRuntime Runtime, ModelProvider Provider, string Model, string? Instructions,
        IReadOnlyList<AgentSkill>? Skills, Guid? OwnerUserId, Guid? ProjectId, bool IsDefault, bool Enabled, int MaxConcurrentRuns, decimal? MonthlyBudgetUsd,
        AgentSettings? Settings);
    public sealed record StartRunRequest(Guid ProjectId, Guid AgentId, int? WorkItemAzureId, Guid? PullRequestId);

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var agents = app.MapGroup("/api/agents").WithTags("Agents").RequireAuthorization();
        agents.MapGet("/", (bool? mine, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<IReadOnlyList<AgentDto>>(new ListAgentsQuery(mine == true), ct));
        agents.MapPost("/", (SaveAgentRequest r, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<AgentDto>(ToCommand(null, r), ct));
        agents.MapPut("/{agentId:guid}", (Guid agentId, SaveAgentRequest r, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<AgentDto>(ToCommand(agentId, r), ct));
        agents.MapDelete("/{agentId:guid}", async (Guid agentId, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new DeleteAgentCommand(agentId), ct);
            return Results.NoContent();
        });
        app.MapGet("/api/agent-runtimes", (IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<IReadOnlyList<AgentRuntimeInfo>>(new ListAgentRuntimesQuery(), ct))
            .RequireAuthorization().WithTags("Agents");

        var runs = app.MapGroup("/api/runs").WithTags("Runs").RequireAuthorization();
        runs.MapGet("/", (Guid? project, RunStatus? status, Guid? agent, bool? mine, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<RunSummaryDto>>(new ListRunsQuery(project, status, agent, mine == true), ct));
        runs.MapPost("/", async (StartRunRequest r, IMessageBus bus, CancellationToken ct) =>
            new { id = await bus.InvokeAsync<Guid>(new StartRunCommand(r.ProjectId, r.AgentId, r.WorkItemAzureId, r.PullRequestId), ct) });
        runs.MapGet("/{runId:guid}", (Guid runId, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<RunDetailDto>(new GetRunQuery(runId), ct));
        runs.MapPost("/{runId:guid}/approve", async (Guid runId, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync<ExecuteAgentRun>(new ApproveRunCommand(runId), ct);
            return Results.NoContent();
        });
        runs.MapPost("/{runId:guid}/cancel", async (Guid runId, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new CancelRunCommand(runId), ct);
            return Results.NoContent();
        });
        runs.MapPost("/{runId:guid}/retry", async (Guid runId, IMessageBus bus, CancellationToken ct) =>
            new { id = await bus.InvokeAsync<Guid>(new RetryRunCommand(runId), ct) });

        var costs = app.MapGroup("/api/costs").WithTags("Costs").RequireAuthorization();
        costs.MapGet("/", (Guid? project, DateTimeOffset? from, DateTimeOffset? to, CostGrouping? groupBy, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<CostReport>(new GetCostReportQuery(project, from, to, groupBy ?? CostGrouping.Agent), ct));
        costs.MapGet("/budgets", (IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<IReadOnlyList<BudgetLine>>(new GetBudgetStatusQuery(), ct));
    }

    private static SaveAgentCommand ToCommand(Guid? agentId, SaveAgentRequest r) =>
        new(agentId, r.Name, r.Title, r.Kind, r.Runtime, r.Provider, r.Model, r.Instructions, r.Skills, r.OwnerUserId, r.ProjectId, r.IsDefault, r.Enabled,
            r.MaxConcurrentRuns, r.MonthlyBudgetUsd, r.Settings);
}
