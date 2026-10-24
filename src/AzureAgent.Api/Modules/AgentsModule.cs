using AzureAgent.Application.Features.Agents;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Application.Features.Costs;
using AzureAgent.Application.Features.Runs;
using AzureAgent.Domain.Agents;
using Carter;
using Wolverine;

namespace AzureAgent.Api.Modules;

public sealed class AgentsModule : ICarterModule
{
    public sealed record SaveAgentRequest(string Name, AgentKind Kind, ModelProvider Provider, string Model, string? Instructions, string? PairedUniqueName,
        Guid? TeamId, bool Enabled, int MaxConcurrentRuns, decimal? MonthlyBudgetUsd, AgentSettings? Settings);
    public sealed record ProvisionRequest(ModelProvider Provider, string Model, IReadOnlyList<string>? SkillKeys);
    public sealed record CapabilitiesRequest(IReadOnlyList<string> SkillKeys, IReadOnlyList<string> McpServerNames);
    public sealed record StartRunRequest(Guid AgentId, int? WorkItemAzureId, Guid? PullRequestId);
    public sealed record SkillRequest(string Name, string? Description, string Content);
    public sealed record McpRequest(McpTransport Transport, string? Command, IReadOnlyList<string>? Arguments, string? Url, IReadOnlyList<string>? SecretNames);

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/projects/{projectId:guid}").WithTags("Agents").RequireAuthorization();
        p.MapGet("/agents", (Guid projectId, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<IReadOnlyList<AgentDto>>(new ListAgentsQuery(projectId), ct));
        p.MapPost("/agents", (Guid projectId, SaveAgentRequest r, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<AgentDto>(ToCommand(projectId, null, r), ct));
        p.MapPut("/agents/{agentId:guid}", (Guid projectId, Guid agentId, SaveAgentRequest r, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<AgentDto>(ToCommand(projectId, agentId, r), ct));
        p.MapDelete("/agents/{agentId:guid}", async (Guid projectId, Guid agentId, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new DeleteAgentCommand(projectId, agentId), ct);
            return Results.NoContent();
        });
        p.MapPut("/agents/{agentId:guid}/capabilities", async (Guid projectId, Guid agentId, CapabilitiesRequest r, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new SetAgentCapabilitiesCommand(projectId, agentId, r.SkillKeys, r.McpServerNames), ct);
            return Results.NoContent();
        });
        p.MapPost("/teams/{teamId:guid}/provision-agents", async (Guid projectId, Guid teamId, ProvisionRequest r, IMessageBus bus, CancellationToken ct) =>
            new { created = await bus.InvokeAsync<int>(new ProvisionTeamAgentsCommand(projectId, teamId, r.Provider, r.Model, r.SkillKeys), ct) });
        p.MapGet("/skill-suggestions", (Guid projectId, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<SkillSuggestionDto>>(new SuggestSkillsQuery(projectId), ct));

        p.MapGet("/runs", (Guid projectId, RunStatus? status, Guid? agentId, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<RunSummaryDto>>(new ListRunsQuery(projectId, status, agentId), ct));
        p.MapGet("/runs/{runId:guid}", (Guid projectId, Guid runId, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<RunDetailDto>(new GetRunQuery(projectId, runId), ct));
        p.MapPost("/runs", async (Guid projectId, StartRunRequest r, IMessageBus bus, CancellationToken ct) =>
            new { id = await bus.InvokeAsync<Guid>(new StartRunCommand(projectId, r.AgentId, r.WorkItemAzureId, r.PullRequestId), ct) });
        p.MapPost("/runs/{runId:guid}/approve", async (Guid projectId, Guid runId, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync<ExecuteAgentRun>(new ApproveRunCommand(projectId, runId), ct);
            return Results.NoContent();
        });
        p.MapPost("/runs/{runId:guid}/cancel", async (Guid projectId, Guid runId, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new CancelRunCommand(projectId, runId), ct);
            return Results.NoContent();
        });
        p.MapPost("/runs/{runId:guid}/retry", async (Guid projectId, Guid runId, IMessageBus bus, CancellationToken ct) =>
            new { id = await bus.InvokeAsync<Guid>(new RetryRunCommand(projectId, runId), ct) });

        p.MapGet("/costs", (Guid projectId, DateTimeOffset? from, DateTimeOffset? to, CostGrouping? groupBy, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<CostReport>(new GetCostReportQuery(projectId, from, to, groupBy ?? CostGrouping.Agent), ct));
        p.MapGet("/budgets", (Guid projectId, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<IReadOnlyList<BudgetLine>>(new GetBudgetStatusQuery(projectId), ct));

        var org = app.MapGroup("/api").WithTags("Catalog").RequireAuthorization();
        org.MapGet("/skills", (IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<IReadOnlyList<SkillDto>>(new ListSkillsQuery(), ct));
        org.MapPut("/skills/{key}", (string key, SkillRequest r, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<SkillDto>(new SaveSkillCommand(key, r.Name, r.Description, r.Content), ct));
        org.MapDelete("/skills/{key}", async (string key, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new DeleteSkillCommand(key), ct);
            return Results.NoContent();
        });
        org.MapGet("/mcp-servers", (IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<IReadOnlyList<McpServerDto>>(new ListMcpServersQuery(), ct));
        org.MapPut("/mcp-servers/{name}", (string name, McpRequest r, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<McpServerDto>(new SaveMcpServerCommand(name, r.Transport, r.Command, r.Arguments, r.Url, r.SecretNames), ct));
        org.MapDelete("/mcp-servers/{name}", async (string name, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new DeleteMcpServerCommand(name), ct);
            return Results.NoContent();
        });
        org.MapGet("/catalog", (IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<CatalogDto>(new GetCatalogQuery(), ct));
        org.MapGet("/model-prices", (IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<IReadOnlyList<ModelPriceDto>>(new ListModelPricesQuery(), ct));
        org.MapPut("/model-prices", async (SaveModelPriceCommand c, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(c, ct);
            return Results.NoContent();
        });
    }

    private static SaveAgentCommand ToCommand(Guid projectId, Guid? agentId, SaveAgentRequest r) =>
        new(projectId, agentId, r.Name, r.Kind, r.Provider, r.Model, r.Instructions, r.PairedUniqueName, r.TeamId, r.Enabled, r.MaxConcurrentRuns, r.MonthlyBudgetUsd, r.Settings);
}
