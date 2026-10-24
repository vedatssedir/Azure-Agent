using AzureAgent.Application.Features.Audit;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Application.Features.Projects;
using AzureAgent.Application.Features.Secrets;
using AzureAgent.Domain.Azure;
using AzureAgent.Domain.Projects;
using Carter;
using Wolverine;

namespace AzureAgent.Api.Modules;

public sealed class ProjectsModule : ICarterModule
{
    public sealed record SaveProjectRequest(string Name, string? Description, string AzureOrganizationUrl, string AzureProjectName, decimal? MonthlyBudgetUsd, ProjectSettings Settings);
    public sealed record MemberRequest(ProjectRole Role, string? AzureUniqueName);
    public sealed record SecretRequest(string Value);
    public sealed record AccessRuleRequest(Guid? UserId, ProjectRole? Role, string Permission, AccessEffect Effect);
    public sealed record AssignRequest(bool ToMe, string? UniqueName);

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/projects").WithTags("Projects").RequireAuthorization();
        projects.MapGet("/", (IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<IReadOnlyList<ProjectDto>>(new ListProjectsQuery(), ct));
        projects.MapPost("/", (CreateProjectCommand c, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<ProjectDto>(c, ct));
        projects.MapGet("/{projectId:guid}", (Guid projectId, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<ProjectDto>(new GetProjectQuery(projectId), ct));
        projects.MapPut("/{projectId:guid}", (Guid projectId, SaveProjectRequest r, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<ProjectDto>(new UpdateProjectCommand(projectId, r.Name, r.Description, r.AzureOrganizationUrl, r.AzureProjectName, r.MonthlyBudgetUsd, r.Settings), ct));

        projects.MapGet("/{projectId:guid}/members", (Guid projectId, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<ProjectMemberDto>>(new ListProjectMembersQuery(projectId), ct));
        projects.MapPut("/{projectId:guid}/members/{userId:guid}", (Guid projectId, Guid userId, MemberRequest r, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<ProjectMemberDto>(new SetProjectMemberCommand(projectId, userId, r.Role, r.AzureUniqueName), ct));
        projects.MapDelete("/{projectId:guid}/members/{userId:guid}", async (Guid projectId, Guid userId, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new RemoveProjectMemberCommand(projectId, userId), ct);
            return Results.NoContent();
        });

        projects.MapGet("/{projectId:guid}/secrets", (Guid projectId, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<SecretDto>>(new ListSecretsQuery(projectId), ct));
        projects.MapPut("/{projectId:guid}/secrets/{name}", async (Guid projectId, string name, SecretRequest r, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new SetSecretCommand(projectId, name, r.Value), ct);
            return Results.NoContent();
        });
        projects.MapDelete("/{projectId:guid}/secrets/{name}", async (Guid projectId, string name, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new DeleteSecretCommand(projectId, name), ct);
            return Results.NoContent();
        });

        projects.MapPost("/{projectId:guid}/azure/test", (Guid projectId, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<ConnectionCheck>>(new TestAzureConnection(projectId), ct));
        projects.MapPost("/{projectId:guid}/azure/sync", (Guid projectId, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<SyncResult>(new RequestProjectSync(projectId), ct));

        projects.MapGet("/{projectId:guid}/teams", (Guid projectId, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<TeamDto>>(new ListTeamsQuery(projectId), ct));
        projects.MapGet("/{projectId:guid}/work-items", (Guid projectId, bool? mine, string? state, string? search, string? iteration, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<WorkItemSummaryDto>>(new ListWorkItemsQuery(projectId, mine == true, state, search, iteration), ct));
        projects.MapGet("/{projectId:guid}/work-items/{azureId:int}", (Guid projectId, int azureId, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<WorkItemDetailDto>(new GetWorkItemQuery(projectId, azureId), ct));
        projects.MapPost("/{projectId:guid}/work-items/{azureId:int}/assign", (Guid projectId, int azureId, AssignRequest r, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<WorkItemSummaryDto>(new AssignWorkItemCommand(projectId, azureId, r.ToMe, r.UniqueName), ct));

        projects.MapGet("/{projectId:guid}/repositories", (Guid projectId, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<RepositoryDto>>(new ListRepositoriesQuery(projectId), ct));
        projects.MapPut("/{projectId:guid}/repositories/{repositoryId:guid}/policy", async (Guid projectId, Guid repositoryId, RepositoryPolicy policy, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new UpdateRepositoryPolicyCommand(projectId, repositoryId, policy), ct);
            return Results.NoContent();
        });
        projects.MapGet("/{projectId:guid}/repositories/{repositoryId:guid}/access-rules", (Guid projectId, Guid repositoryId, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<RepositoryAccessRuleDto>>(new ListRepositoryAccessRulesQuery(projectId, repositoryId), ct));
        projects.MapPost("/{projectId:guid}/repositories/{repositoryId:guid}/access-rules", (Guid projectId, Guid repositoryId, AccessRuleRequest r, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<RepositoryAccessRuleDto>(new AddRepositoryAccessRuleCommand(projectId, repositoryId, r.UserId, r.Role, r.Permission, r.Effect), ct));
        projects.MapDelete("/{projectId:guid}/repositories/{repositoryId:guid}/access-rules/{ruleId:guid}", async (Guid projectId, Guid repositoryId, Guid ruleId, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new RemoveRepositoryAccessRuleCommand(projectId, repositoryId, ruleId), ct);
            return Results.NoContent();
        });
        projects.MapGet("/{projectId:guid}/repositories/{repositoryId:guid}/commits", (Guid projectId, Guid repositoryId, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<CommitDto>>(new ListCommitsQuery(projectId, repositoryId), ct));
        projects.MapGet("/{projectId:guid}/pull-requests", (Guid projectId, PullRequestStatus? status, Guid? repositoryId, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<PullRequestDto>>(new ListPullRequestsQuery(projectId, status, repositoryId), ct));

        projects.MapGet("/{projectId:guid}/audit", (Guid projectId, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<AuditEntryDto>>(new ListAuditQuery(projectId), ct));
        projects.MapGet("/permissions", () => Permission.All).AllowAnonymous();
    }
}
