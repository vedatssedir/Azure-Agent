namespace AzureAgent.Domain.Projects;

/// <summary>
/// Fine-grained permissions. Project roles grant sets of these; repository rules can grant
/// or deny individual permissions per repository (deny wins).
/// </summary>
public static class Permission
{
    public const string ProjectView = "project.view";
    public const string ProjectManage = "project.manage";
    public const string MembersManage = "project.members.manage";
    public const string SettingsManage = "project.settings.manage";
    public const string SecretsManage = "project.secrets.manage";

    public const string WorkItemsView = "workitems.view";
    public const string WorkItemsAssign = "workitems.assign";
    public const string WorkItemsCreate = "workitems.create";

    public const string RepositoryView = "repo.view";
    public const string RepositoryAgentWrite = "repo.agent.write";
    public const string RepositoryPolicyManage = "repo.policy.manage";

    public const string PullRequestsReview = "pullrequests.review";

    public const string AgentsView = "agents.view";
    public const string AgentsManage = "agents.manage";
    public const string AgentsRun = "agents.run";

    public const string CostsView = "costs.view";
    public const string BudgetsManage = "budgets.manage";
    public const string AuditView = "audit.view";

    public static readonly IReadOnlyList<string> All =
    [
        ProjectView, ProjectManage, MembersManage, SettingsManage, SecretsManage,
        WorkItemsView, WorkItemsAssign, WorkItemsCreate,
        RepositoryView, RepositoryAgentWrite, RepositoryPolicyManage,
        PullRequestsReview,
        AgentsView, AgentsManage, AgentsRun,
        CostsView, BudgetsManage, AuditView,
    ];

    /// <summary>Permissions a repository access rule may grant or deny.</summary>
    public static readonly IReadOnlySet<string> RepositoryScoped = new HashSet<string>
    {
        RepositoryView, RepositoryAgentWrite, RepositoryPolicyManage, PullRequestsReview,
    };
}

public enum ProjectRole
{
    Viewer = 0,
    Developer = 1,
    Reviewer = 2,
    Maintainer = 3,
    Admin = 4,
}

public static class RolePermissions
{
    private static readonly string[] Viewer =
    [
        Permission.ProjectView, Permission.WorkItemsView, Permission.RepositoryView, Permission.AgentsView,
    ];

    private static readonly string[] Developer =
    [
        .. Viewer, Permission.WorkItemsAssign, Permission.WorkItemsCreate, Permission.RepositoryAgentWrite, Permission.AgentsRun,
    ];

    private static readonly string[] Reviewer = [.. Developer, Permission.PullRequestsReview];

    private static readonly string[] Maintainer =
    [
        .. Reviewer, Permission.AgentsManage, Permission.RepositoryPolicyManage, Permission.CostsView,
        Permission.SettingsManage, Permission.AuditView,
    ];

    public static IReadOnlySet<string> For(ProjectRole role) => role switch
    {
        ProjectRole.Viewer => Viewer.ToHashSet(),
        ProjectRole.Developer => Developer.ToHashSet(),
        ProjectRole.Reviewer => Reviewer.ToHashSet(),
        ProjectRole.Maintainer => Maintainer.ToHashSet(),
        ProjectRole.Admin => Permission.All.ToHashSet(),
        _ => new HashSet<string>(),
    };
}
