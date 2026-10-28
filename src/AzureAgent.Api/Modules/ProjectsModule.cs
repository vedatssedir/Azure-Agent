using AzureAgent.Application.Features.Azure;
using AzureAgent.Application.Features.Projects;
using Carter;
using Wolverine;

namespace AzureAgent.Api.Modules;

/// <summary>Synced Azure DevOps projects and their boards (work items, Kanban, sprints).</summary>
public sealed class ProjectsModule : ICarterModule
{
    public sealed record AssignRequest(bool ToMe, string? UniqueName);
    public sealed record MoveRequest(Guid TeamId, string Board, string Column);
    public sealed record CommentRequest(string Text);
    public sealed record UpdateWorkItemRequest(string? Title, string? Description, string? AcceptanceCriteria, string? State, string? IterationPath, int? Priority,
        IReadOnlyList<string>? Tags);
    public sealed record CreateWorkItemRequest(string Type, string Title, string? Description, string? AcceptanceCriteria, IReadOnlyList<string> RepositoryNames,
        IReadOnlyList<string>? Tags, bool AssignToMe, int? Priority, string? IterationPath, string? AgentSlug);

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/work-items/mine", (bool? includeClosed, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<MyWorkItemDto>>(new ListMyWorkItemsQuery(includeClosed == true), ct)).RequireAuthorization().WithTags("Boards");

        var projects = app.MapGroup("/api/projects").WithTags("Projects").RequireAuthorization();
        projects.MapGet("/", (IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<IReadOnlyList<ProjectDto>>(new ListProjectsQuery(), ct));
        projects.MapGet("/{projectId:guid}", (Guid projectId, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<ProjectDto>(new GetProjectQuery(projectId), ct));
        projects.MapGet("/{projectId:guid}/teams", (Guid projectId, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<TeamDto>>(new ListTeamsQuery(projectId), ct));
        projects.MapGet("/{projectId:guid}/work-item-types", (Guid projectId, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<WorkItemTypeDto>>(new ListWorkItemTypesQuery(projectId), ct));

        projects.MapGet("/{projectId:guid}/board", (Guid projectId, Guid? team, string? board, bool? mine, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<BoardDto>(new GetBoardQuery(projectId, team, board, mine == true), ct));
        projects.MapGet("/{projectId:guid}/sprint", (Guid projectId, Guid? team, string? iteration, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<SprintDto>(new GetSprintQuery(projectId, team, iteration), ct));
        projects.MapGet("/{projectId:guid}/work-items", (Guid projectId, bool? mine, string? state, string? search, string? iteration, Guid? team, bool? unassigned,
                string? type, bool? includeClosed, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<WorkItemSummaryDto>>(new ListWorkItemsQuery(projectId, mine == true, state, search, iteration, team, unassigned == true, type,
                includeClosed != false), ct));
        projects.MapPost("/{projectId:guid}/work-items", (Guid projectId, CreateWorkItemRequest r, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<WorkItemSummaryDto>(new CreateWorkItemCommand(projectId, r.Type, r.Title, r.Description, r.AcceptanceCriteria, r.RepositoryNames,
                r.Tags, r.AssignToMe, r.Priority, r.IterationPath, r.AgentSlug), ct));
        projects.MapGet("/{projectId:guid}/work-items/{azureId:int}", (Guid projectId, int azureId, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<WorkItemDetailDto>(new GetWorkItemQuery(projectId, azureId), ct));
        projects.MapPatch("/{projectId:guid}/work-items/{azureId:int}", (Guid projectId, int azureId, UpdateWorkItemRequest r, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<WorkItemSummaryDto>(new UpdateWorkItemCommand(projectId, azureId, r.Title, r.Description, r.AcceptanceCriteria, r.State, r.IterationPath,
                r.Priority, r.Tags), ct));
        projects.MapPost("/{projectId:guid}/work-items/{azureId:int}/assign", (Guid projectId, int azureId, AssignRequest r, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<WorkItemSummaryDto>(new AssignWorkItemCommand(projectId, azureId, r.ToMe, r.UniqueName), ct));
        projects.MapPost("/{projectId:guid}/work-items/{azureId:int}/move", (Guid projectId, int azureId, MoveRequest r, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<WorkItemSummaryDto>(new MoveWorkItemCommand(projectId, azureId, r.TeamId, r.Board, r.Column), ct));
        projects.MapGet("/{projectId:guid}/work-items/{azureId:int}/comments", (Guid projectId, int azureId, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<CommentDto>>(new ListWorkItemCommentsQuery(projectId, azureId), ct));
        projects.MapPost("/{projectId:guid}/work-items/{azureId:int}/comments", async (Guid projectId, int azureId, CommentRequest r, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new AddWorkItemCommentCommand(projectId, azureId, r.Text), ct);
            return Results.NoContent();
        });
    }
}
