using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Domain.Agents;
using AzureAgent.Domain.Azure;
using AzureAgent.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Features.Repos;

public sealed record RepositoryDto(Guid Id, Guid ProjectId, string ProjectName, string Name, string? DefaultBranch, string WebUrl, long SizeBytes,
    DateTimeOffset? MirrorSyncedAt, string? MirrorError, int OpenPullRequests, RepositoryPolicy Policy);

internal static class RepositoryAccess
{
    /// <summary>The repository, if the caller may view it (project role + repository rules).</summary>
    public static async Task<Repository> RequireAsync(IAppDbContext db, AccessControl access, Guid repositoryId, CancellationToken ct, string permission = Permission.RepositoryView)
    {
        var repo = await db.Repositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == repositoryId && !r.IsRemoved, ct) ?? throw new NotFoundException("Repository");
        var permissions = await access.PermissionsAsync(repo.ProjectId, repo.Id, ct);
        if (!permissions.Contains(permission))
        {
            if (!permissions.Contains(Permission.RepositoryView)) throw new NotFoundException("Repository");
            throw new ForbiddenException($"Missing permission '{permission}'");
        }
        return repo;
    }
}

/// <summary>Every repository of the organization the caller can see (the Repos menu).</summary>
public sealed record ListRepositoriesQuery(Guid? ProjectId = null);

public static class ListRepositoriesHandler
{
    public static async Task<IReadOnlyList<RepositoryDto>> Handle(ListRepositoriesQuery query, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        access.RequireOrganization();
        var repos = await db.Repositories.AsNoTracking().Where(r => !r.IsRemoved && (query.ProjectId == null || r.ProjectId == query.ProjectId))
            .Join(db.Projects.Where(p => !p.IsRemoved), r => r.ProjectId, p => p.Id, (r, p) => new { r, ProjectName = p.Name })
            .OrderBy(x => x.ProjectName).ThenBy(x => x.r.Name)
            .Select(x => new RepositoryDto(x.r.Id, x.r.ProjectId, x.ProjectName, x.r.Name, x.r.DefaultBranch, x.r.WebUrl, x.r.SizeBytes, x.r.MirrorSyncedAt,
                x.r.MirrorError, db.PullRequests.Count(p => p.RepositoryId == x.r.Id && p.Status == PullRequestStatus.Active), x.r.Policy))
            .ToListAsync(ct);
        var visible = new List<RepositoryDto>();
        foreach (var repo in repos)
            if ((await access.PermissionsAsync(repo.ProjectId, repo.Id, ct)).Contains(Permission.RepositoryView)) visible.Add(repo);
        return visible;
    }
}

public sealed record RepositoryDetailDto(RepositoryDto Repository, IReadOnlyList<GitBranch> Branches, bool CanChangePolicy);

public sealed record GetRepositoryQuery(Guid RepositoryId);

public static class GetRepositoryHandler
{
    public static async Task<RepositoryDetailDto> Handle(GetRepositoryQuery query, IAppDbContext db, AccessControl access, IRepositoryMirror mirror, CancellationToken ct)
    {
        var repo = await RepositoryAccess.RequireAsync(db, access, query.RepositoryId, ct);
        var dto = (await ListRepositoriesHandler.Handle(new ListRepositoriesQuery(repo.ProjectId), db, access, ct)).Single(r => r.Id == repo.Id);
        var branches = repo.DefaultBranch is not null && mirror.Exists(repo.OrganizationId, repo.Id) ? await mirror.BranchesAsync(repo.OrganizationId, repo.Id, ct) : [];
        var canChange = (await access.PermissionsAsync(repo.ProjectId, repo.Id, ct)).Contains(Permission.RepositoryPolicyManage);
        return new RepositoryDetailDto(dto, branches, canChange);
    }
}

public sealed record GetTreeQuery(Guid RepositoryId, string? Ref, string? Path);

public static class GetTreeHandler
{
    public static async Task<IReadOnlyList<GitTreeEntry>> Handle(GetTreeQuery query, IAppDbContext db, AccessControl access, IRepositoryMirror mirror, CancellationToken ct)
    {
        var repo = await RepositoryAccess.RequireAsync(db, access, query.RepositoryId, ct);
        if (repo.DefaultBranch is null) return [];
        return await mirror.TreeAsync(repo.OrganizationId, repo.Id, query.Ref ?? repo.DefaultBranch, query.Path ?? "", ct);
    }
}

public sealed record GetFileQuery(Guid RepositoryId, string? Ref, string Path);

public static class GetFileHandler
{
    public static async Task<GitBlob> Handle(GetFileQuery query, IAppDbContext db, AccessControl access, IRepositoryMirror mirror, CancellationToken ct)
    {
        var repo = await RepositoryAccess.RequireAsync(db, access, query.RepositoryId, ct);
        if (repo.DefaultBranch is null) throw new NotFoundException("File");
        return await mirror.BlobAsync(repo.OrganizationId, repo.Id, query.Ref ?? repo.DefaultBranch, query.Path, ct) ?? throw new NotFoundException("File");
    }
}

public sealed record ListCommitsQuery(Guid RepositoryId, string? Ref, string? Path, int Skip = 0, int Take = 50);

public static class ListCommitsHandler
{
    public static async Task<IReadOnlyList<GitCommitInfo>> Handle(ListCommitsQuery query, IAppDbContext db, AccessControl access, IRepositoryMirror mirror, CancellationToken ct)
    {
        var repo = await RepositoryAccess.RequireAsync(db, access, query.RepositoryId, ct);
        if (repo.DefaultBranch is null) return [];
        return await mirror.LogAsync(repo.OrganizationId, repo.Id, query.Ref ?? repo.DefaultBranch, query.Path, query.Skip, query.Take, ct);
    }
}

public sealed record GetCommitQuery(Guid RepositoryId, string CommitId);

public static class GetCommitHandler
{
    public static async Task<GitCommitDetail> Handle(GetCommitQuery query, IAppDbContext db, AccessControl access, IRepositoryMirror mirror, CancellationToken ct)
    {
        var repo = await RepositoryAccess.RequireAsync(db, access, query.RepositoryId, ct);
        return await mirror.CommitAsync(repo.OrganizationId, repo.Id, query.CommitId, ct) ?? throw new NotFoundException("Commit");
    }
}

// Pull requests -----------------------------------------------------------------------------------------

public sealed record ListPullRequestsQuery(Guid? ProjectId, Guid? RepositoryId, PullRequestStatus? Status);

public static class ListPullRequestsHandler
{
    public static async Task<IReadOnlyList<PullRequestDto>> Handle(ListPullRequestsQuery query, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        access.RequireOrganization();
        var visible = (await ListRepositoriesHandler.Handle(new ListRepositoriesQuery(query.ProjectId), db, access, ct)).Select(r => r.Id).ToList();
        if (query.RepositoryId is { } repositoryId) visible = visible.Where(id => id == repositoryId).ToList();
        return await db.PullRequests.AsNoTracking().Where(p => visible.Contains(p.RepositoryId) && (query.Status == null || p.Status == query.Status))
            .OrderByDescending(p => p.OpenedAt).Take(300).PullRequestSummaries(db).ToListAsync(ct);
    }
}

public sealed record ReviewDto(Guid RunId, string AgentName, ReviewVerdict Verdict, string Summary, IReadOnlyList<ReviewFinding> Findings, string SourceCommit,
    DateTimeOffset CreatedAt);

public sealed record PullRequestDetailDto(PullRequestDto PullRequest, IReadOnlyList<ReviewDto> Reviews, IReadOnlyList<RunSummaryDto> Runs,
    IReadOnlyList<GitFileChange> Files);

public sealed record GetPullRequestQuery(Guid PullRequestId);

public static class GetPullRequestHandler
{
    public static async Task<PullRequestDetailDto> Handle(GetPullRequestQuery query, IAppDbContext db, AccessControl access, IRepositoryMirror mirror, CancellationToken ct)
    {
        var pr = await db.PullRequests.AsNoTracking().FirstOrDefaultAsync(p => p.Id == query.PullRequestId, ct) ?? throw new NotFoundException("Pull request");
        var repo = await RepositoryAccess.RequireAsync(db, access, pr.RepositoryId, ct);
        var dto = await db.PullRequests.AsNoTracking().Where(p => p.Id == pr.Id).PullRequestSummaries(db).SingleAsync(ct);
        var reviews = await db.CodeReviews.AsNoTracking().Where(c => c.PullRequestId == pr.Id).OrderByDescending(c => c.CreatedAt)
            .Join(db.AgentRuns, c => c.RunId, r => r.Id, (c, r) => new { c, r.AgentId })
            .Join(db.Agents, x => x.AgentId, a => a.Id, (x, a) => new ReviewDto(x.c.RunId, a.Name, x.c.Verdict, x.c.Summary, x.c.Findings, x.c.SourceCommit, x.c.CreatedAt))
            .ToListAsync(ct);
        var runs = await db.AgentRuns.AsNoTracking().Where(r => r.PullRequestId == pr.Id).OrderByDescending(r => r.CreatedAt).RunSummaries(db).ToListAsync(ct);
        IReadOnlyList<GitFileChange> files = [];
        if (mirror.Exists(repo.OrganizationId, repo.Id))
        {
            try { files = await mirror.CompareAsync(repo.OrganizationId, repo.Id, pr.TargetBranch, pr.SourceBranch, ct); }
            catch (AppException) { files = []; } // the branch was deleted after completion
        }
        return new PullRequestDetailDto(dto, reviews, runs, files);
    }
}
