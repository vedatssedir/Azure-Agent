using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Application.Features.Repos;
using AzureAgent.Domain.Azure;
using Carter;
using Wolverine;

namespace AzureAgent.Api.Modules;

/// <summary>Code browser, history and pull requests, read from the local mirrors.</summary>
public sealed class ReposModule : ICarterModule
{
    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var repos = app.MapGroup("/api/repos").WithTags("Repos").RequireAuthorization();
        repos.MapGet("/", (Guid? project, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<IReadOnlyList<RepositoryDto>>(new ListRepositoriesQuery(project), ct));
        repos.MapGet("/{repositoryId:guid}", (Guid repositoryId, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<RepositoryDetailDto>(new GetRepositoryQuery(repositoryId), ct));
        repos.MapGet("/{repositoryId:guid}/tree", (Guid repositoryId, string? @ref, string? path, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<GitTreeEntry>>(new GetTreeQuery(repositoryId, @ref, path), ct));
        repos.MapGet("/{repositoryId:guid}/file", (Guid repositoryId, string? @ref, string path, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<GitBlob>(new GetFileQuery(repositoryId, @ref, path), ct));
        repos.MapGet("/{repositoryId:guid}/commits", (Guid repositoryId, string? @ref, string? path, int? skip, int? take, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<GitCommitInfo>>(new ListCommitsQuery(repositoryId, @ref, path, skip ?? 0, take ?? 50), ct));
        repos.MapGet("/{repositoryId:guid}/commits/{commitId}", (Guid repositoryId, string commitId, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<GitCommitDetail>(new GetCommitQuery(repositoryId, commitId), ct));

        var prs = app.MapGroup("/api/pull-requests").WithTags("Pull requests").RequireAuthorization();
        prs.MapGet("/", (Guid? project, Guid? repository, PullRequestStatus? status, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<PullRequestDto>>(new ListPullRequestsQuery(project, repository, status), ct));
        prs.MapGet("/{pullRequestId:guid}", (Guid pullRequestId, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<PullRequestDetailDto>(new GetPullRequestQuery(pullRequestId), ct));
    }
}
