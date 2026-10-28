using System.Net;
using System.Text.Json;
using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Features.Agents;
using AzureAgent.Application.Features.Auth;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Application.Features.Costs;
using AzureAgent.Application.Features.Identity;
using AzureAgent.Application.Features.Projects;
using AzureAgent.Application.Features.Repos;
using AzureAgent.Application.Features.Runs;
using AzureAgent.Application.Features.Settings;
using AzureAgent.Domain.Agents;
using AzureAgent.Domain.Azure;
using AzureAgent.Domain.Identity;
using AzureAgent.Domain.Projects;
using AzureAgent.IntegrationTests.Fakes;
using Shouldly;

namespace AzureAgent.IntegrationTests;

[Collection(PlatformCollection.Name)]
public sealed class PlatformTests(AppFactory factory) : PlatformTestBase(factory)
{
    [Fact]
    public async Task Setup_imports_the_whole_azure_organization_with_boards_sprints_and_repository_history()
    {
        var (admin, _) = await AdminAsync();
        (await NewApi().Get<SetupStatusDto>("/api/auth/status")).SetupRequired.ShouldBeFalse();
        (await NewApi().Send(HttpMethod.Post, "/api/auth/setup", new
        {
            email = "x@contoso.com", displayName = "X", password = Password, azureOrganizationUrl = OrganizationUrl, personalAccessToken = AdminPat,
        })).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var settings = await admin.Get<SettingsDto>("/api/settings");
        settings.Azure.ShouldSatisfyAllConditions(
            a => a.OrganizationName.ShouldBe("contoso"), a => a.OrganizationUrl.ShouldBe(OrganizationUrl), a => a.PatConfigured.ShouldBeTrue(),
            a => a.Projects.ShouldBeGreaterThanOrEqualTo(1), a => a.MirroredRepositories.ShouldBeGreaterThanOrEqualTo(1));
        settings.Providers.Single(p => p.Key == "anthropic").Configured.ShouldBeTrue();
        // The administrator's PAT belongs to them, so it is also their personal Azure connection.
        (await admin.Get<List<AzureCredentialDto>>("/api/me/azure-credentials")).Single().AzureUniqueName.ShouldBe("admin@contoso.com");

        var shop = await ShopAsync(admin);
        shop.ShouldSatisfyAllConditions(p => p.Key.ShouldBe("SHOP"), p => p.Repositories.ShouldBe(1), p => p.Teams.ShouldBe(1), p => p.OpenWorkItems.ShouldBe(1));

        // Process, teams, Kanban boards (columns, WIP limits, state mappings) and sprints come from Azure.
        var types = await admin.Get<List<WorkItemTypeDto>>($"/api/projects/{shop.Id}/work-item-types");
        types.Single(t => t.Name == "Issue").States.Select(s => s.Category).ShouldBe(["Proposed", "InProgress", "Completed"]);
        var team = (await admin.Get<List<TeamDto>>($"/api/projects/{shop.Id}/teams")).Single();
        team.Boards.ShouldBe(["Issues", "Epics"]);
        team.Iterations.Select(i => i.Name).ShouldBe(["Sprint 1", "Sprint 2"]);
        var board = await admin.Get<BoardDto>($"/api/projects/{shop.Id}/board");
        board.Columns.Select(c => c.Name).ShouldBe(["To Do", "Doing", "Review", "Done"]);
        board.Columns.Single(c => c.Name == "Doing").ItemLimit.ShouldBe(5);
        board.Columns.Single(c => c.Name == "To Do").Items.Select(i => i.AzureId).ShouldBe([101]);
        board.Columns.Single(c => c.Name == "Done").Items.Select(i => i.AzureId).ShouldBe([102]);
        board.Columns.Single(c => c.Name == "To Do").Items.Single().ShouldSatisfyAllConditions(i => i.TypeColor.ShouldBe("339947"), i => i.StateCategory.ShouldBe("Proposed"));
        var sprint = await admin.Get<SprintDto>($"/api/projects/{shop.Id}/sprint");
        sprint.Iteration!.Name.ShouldBe("Sprint 1");
        sprint.Items.Select(i => i.AzureId).ShouldBe([101, 102], ignoreOrder: true);

        // Repos: the mirror has the full history; files, commits and diffs are browsable.
        var repo = (await admin.Get<List<RepositoryDto>>($"/api/repos?project={shop.Id}")).Single();
        repo.ShouldSatisfyAllConditions(r => r.Name.ShouldBe("api"), r => r.DefaultBranch.ShouldBe("main"), r => r.MirrorSyncedAt.ShouldNotBeNull(), r => r.MirrorError.ShouldBeNull());
        var tree = await admin.Get<List<GitTreeEntry>>($"/api/repos/{repo.Id}/tree");
        tree.Select(e => (e.Name, e.IsDirectory)).ShouldBe([("src", true), ("README.md", false)], ignoreOrder: true);
        (await admin.Get<GitBlob>($"/api/repos/{repo.Id}/file?path=src/Paging.cs")).Content.ShouldBe("public static class Paging { }\n");
        (await admin.Send(HttpMethod.Get, $"/api/repos/{repo.Id}/file?path=../../etc/passwd")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var commits = await admin.Get<List<GitCommitInfo>>($"/api/repos/{repo.Id}/commits");
        commits.Select(c => c.Subject).ShouldBe(["AB#101 Add paging", "init"]);
        (await admin.Get<List<GitCommitInfo>>($"/api/repos/{repo.Id}/commits?path=src/Paging.cs")).ShouldHaveSingleItem();
        var detail = await admin.Get<GitCommitDetail>($"/api/repos/{repo.Id}/commits/{commits[0].CommitId}");
        detail.Files.Single().ShouldSatisfyAllConditions(f => f.Path.ShouldBe("src/Paging.cs"), f => f.Status.ShouldBe("add"), f => f.Additions.ShouldBe(1));
        // AB# commits are linked to their work items.
        (await admin.Get<WorkItemDetailDto>($"/api/projects/{shop.Id}/work-items/101")).Commits.Single().CommitId.ShouldBe(commits[0].CommitId);
    }

    [Fact]
    public async Task Developer_signs_up_and_their_default_agent_delivers_the_assigned_item()
    {
        var (admin, organizationId) = await AdminAsync();
        var shop = await ShopAsync(admin);
        var bare = Path.Combine(Factory.Root, "remotes", ShopProject, "api.git");

        // Jane is in the Shop team in Azure: signing up with her PAT gives her the project, her board and an agent.
        var jane = await DeveloperAsync("jane@contoso.com", "Jane Doe", organizationId);
        var me = await jane.Get<MeDto>("/api/auth/me");
        me.Organizations.Single().ShouldSatisfyAllConditions(o => o.OrganizationId.ShouldBe(organizationId), o => o.Role.ShouldBe(OrganizationRole.Member));
        (await jane.Get<List<ProjectDto>>("/api/projects")).Single(p => p.Id == shop.Id).MyRole.ShouldBe(ProjectRole.Developer);
        var agent = (await jane.Get<List<AgentDto>>("/api/agents?mine=true")).Single();
        agent.ShouldSatisfyAllConditions(a => a.Name.ShouldBe("Jane's agent"), a => a.IsDefault.ShouldBeTrue(), a => a.Runtime.ShouldBe(AgentRuntime.ClaudeCode),
            a => a.CanEdit.ShouldBeTrue());
        (await jane.Get<List<MyWorkItemDto>>("/api/work-items/mine")).ShouldNotContain(m => m.ProjectId == shop.Id); // #102 is Done
        (await jane.Get<List<MyWorkItemDto>>("/api/work-items/mine?includeClosed=true")).Single(m => m.ProjectId == shop.Id).Item.AzureId.ShouldBe(102);
        (await jane.Send(HttpMethod.Get, "/api/settings")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Skills are the agent's own.
        await jane.Put<AgentDto>($"/api/agents/{agent.Id}", new
        {
            agent.Name, agent.Title, agent.Kind, agent.Runtime, agent.Provider, agent.Model, instructions = "Keep changes small.",
            skills = new[] { new { name = "testing", content = "Always add a unit test.", enabled = true } }, agent.OwnerUserId, agent.ProjectId, agent.IsDefault,
            agent.Enabled, agent.MaxConcurrentRuns, agent.MonthlyBudgetUsd, agent.Settings,
        });

        var log = Directory.CreateDirectory(Path.Combine(Factory.Root, "cli-log", "signup")).FullName;
        Factory.SetCliScenario("claude", $$$"""
            cp "$(sed -n '/--append-system-prompt-file/{n;p}' <(printf '%s\n' "$@"))" "{{{log}}}/instructions.md"
            echo 'public static class Health { public const string Status = "ok"; }' > api/Health.cs
            echo '{"type":"assistant","message":{"id":"m1","usage":{"input_tokens":1000,"output_tokens":100},"content":[{"type":"tool_use","name":"Write","input":{"file_path":"api/Health.cs"}}]}}'
            echo '{"type":"result","subtype":"success","is_error":false,"result":"Added Health.cs.","usage":{"input_tokens":1000,"output_tokens":100}}'
            """);
        // Assigning the item to herself hands it to her default agent.
        var assigned = await jane.Post<WorkItemSummaryDto>($"/api/projects/{shop.Id}/work-items/101/assign", new { toMe = true });
        assigned.AssignedToUniqueName.ShouldBe("jane@contoso.com");
        Azure.Project(ShopProject).Writes.ShouldContain(("assign", 101, "jane@contoso.com", "Basic")); // written with her own PAT
        var run = await WaitForRunAsync(jane, r => r.ProjectId == shop.Id && r.WorkItemAzureId == 101);
        run.Run.Error.ShouldBeNull();
        run.Run.Status.ShouldBe(RunStatus.Succeeded);
        run.Run.AgentId.ShouldBe(agent.Id);
        run.Run.Branch.ShouldBe("ab/101-add-paging");
        run.Run.PullRequestUrls.ShouldBe(["https://dev.azure.com/contoso/Shop/_git/api/pullrequest/1"]);
        run.Run.CostUsd.ShouldBe(0.0045m); // 1000 × 3/1M + 100 × 15/1M
        File.ReadAllText(Path.Combine(log, "instructions.md")).ShouldSatisfyAllConditions(t => t.ShouldContain("Keep changes small."), t => t.ShouldContain("Always add a unit test."));

        var azure = Azure.Project(ShopProject);
        AppFactory.Git(Factory.Root, "--git-dir", bare, "log", "--oneline", "ab/101-add-paging").ShouldContain("AB#101");
        azure.PullRequests.Single().WorkItemId.ShouldBe(101);
        azure.Field(101, "System.State").ShouldBe("Doing"); // the Issue process has no Resolved state; it stays in progress
        azure.Comments.ShouldContain(c => c.WorkItemId == 101 && c.Html.Contains("pullrequest/1"));

        // The next sync brings the PR and the agent's branch into the mirror.
        var sync = await SyncAsync(organizationId, shop.Id);
        sync.QueuedRuns.ShouldBe(0); // delivered items are not dispatched again
        var pr = (await jane.Get<List<PullRequestDto>>($"/api/pull-requests?project={shop.Id}")).Single();
        pr.ShouldSatisfyAllConditions(p => p.CreatedByAgent.ShouldBeTrue(), p => p.WorkItemAzureIds.ShouldBe([101]));
        var prDetail = await jane.Get<PullRequestDetailDto>($"/api/pull-requests/{pr.Id}");
        prDetail.Files.Single().Path.ShouldBe("Health.cs");
        prDetail.Runs.ShouldBeEmpty();
        var repo = (await jane.Get<List<RepositoryDto>>($"/api/repos?project={shop.Id}")).Single();
        (await jane.Get<RepositoryDetailDto>($"/api/repos/{repo.Id}")).Branches.Select(b => b.Name).ShouldContain("ab/101-add-paging");
        var item = await jane.Get<WorkItemDetailDto>($"/api/projects/{shop.Id}/work-items/101");
        item.ShouldSatisfyAllConditions(d => d.Runs.Single().Status.ShouldBe(RunStatus.Succeeded), d => d.PullRequests.Single().Id.ShouldBe(pr.Id),
            d => d.Summary.CostUsd.ShouldBe(0.0045m), d => d.Summary.State.ShouldBe("Doing"));
        (await jane.Get<List<CommentDto>>($"/api/projects/{shop.Id}/work-items/101/comments")).ShouldContain(c => c.Text.Contains("pullrequest/1"));

        var byDeveloper = await admin.Get<CostReport>($"/api/costs?groupBy=Developer&project={shop.Id}");
        byDeveloper.Rows.ShouldContain(r => r.Label == "Jane Doe" && r.CostUsd == 0.0045m);
        (await jane.Send(HttpMethod.Get, "/api/costs/budgets")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Board_moves_follow_column_mappings_and_work_items_are_edited_in_azure()
    {
        var (admin, organizationId) = await AdminAsync();
        var project = await NewProjectAsync(admin, organizationId, "Boards", azure =>
        {
            azure.AddWorkItem(301, "Checkout page", "To Do");
            azure.AddWorkItem(302, "Payments epic", "To Do", type: "Epic");
        });
        var azure = Azure.Project("Boards");
        var board = await admin.Get<BoardDto>($"/api/projects/{project.Id}/board");
        board.Columns.Single(c => c.Name == "To Do").Items.Select(i => i.AzureId).ShouldBe([301]); // epics live on their own board
        (await admin.Get<BoardDto>($"/api/projects/{project.Id}/board?board=Epics")).Columns.First().Items.Select(i => i.AzureId).ShouldBe([302]);

        await admin.Post<WorkItemSummaryDto>($"/api/projects/{project.Id}/work-items/301/move", new { board.TeamId, board = "Issues", column = "Doing" });
        azure.Field(301, "System.State").ShouldBe("Doing");
        // Two columns share the Doing state: the board column field tells them apart.
        await admin.Post<WorkItemSummaryDto>($"/api/projects/{project.Id}/work-items/301/move", new { board.TeamId, board = "Issues", column = "Review" });
        azure.Field(301, "WEF_ISSUES_Kanban.Column").ShouldBe("Review");
        board = await admin.Get<BoardDto>($"/api/projects/{project.Id}/board");
        board.Columns.Single(c => c.Name == "Review").Items.Select(i => i.AzureId).ShouldBe([301]);
        (await admin.Send(HttpMethod.Post, $"/api/projects/{project.Id}/work-items/301/move", new { board.TeamId, board = "Issues", column = "Nope" }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var updated = await admin.Send(HttpMethod.Patch, $"/api/projects/{project.Id}/work-items/301",
            new { title = "Checkout page v2", description = "Line 1\nLine <2>", tags = new[] { "frontend", "repo:web" }, priority = 1, iterationPath = "Boards\\Sprint 2" });
        updated.EnsureSuccessStatusCode();
        azure.Field(301, "System.Title").ShouldBe("Checkout page v2");
        azure.Field(301, "System.Description").ShouldBe("<div>Line 1</div><div>Line &lt;2&gt;</div>");
        azure.Field(301, "System.Tags").ShouldBe("frontend; repo:web");
        azure.Field(301, "System.IterationPath").ShouldBe("Boards\\Sprint 2");
        (await admin.Send(HttpMethod.Patch, $"/api/projects/{project.Id}/work-items/301", new { state = "Nope" })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await admin.Get<SprintDto>($"/api/projects/{project.Id}/sprint?iteration=Boards%5CSprint%202")).Items.Select(i => i.AzureId).ShouldBe([301]);

        (await admin.Send(HttpMethod.Post, $"/api/projects/{project.Id}/work-items/301/comments", new { text = "Looks good" })).EnsureSuccessStatusCode();
        (await admin.Get<List<CommentDto>>($"/api/projects/{project.Id}/work-items/301/comments")).Single().Text.ShouldContain("Looks good");

        // New items go to Azure; an agent tag hands them to that agent.
        await UpdateSettingsAsync(admin, new { requireRunApproval = true });
        try
        {
            var agent = await admin.Post<AgentDto>("/api/agents", new
            {
                name = "Board bot", kind = "Developer", runtime = "Framework", provider = "Anthropic", model = "claude-sonnet-4-5", projectId = project.Id, enabled = true,
                maxConcurrentRuns = 1, isDefault = false,
            });
            agent.Slug.ShouldBe("board-bot");
            var created = await admin.Post<WorkItemSummaryDto>($"/api/projects/{project.Id}/work-items", new
            {
                type = "Issue", title = "Add coupon field", description = "Coupons", repositoryNames = Array.Empty<string>(), assignToMe = false, priority = 2,
                iterationPath = "Boards\\Sprint 1", agentSlug = "board-bot",
            });
            created.Tags.ShouldContain("agent:board-bot");
            created.IterationPath.ShouldBe("Boards\\Sprint 1");
            var pending = (await admin.Get<List<RunSummaryDto>>($"/api/runs?project={project.Id}")).Single(r => r.WorkItemAzureId == created.AzureId);
            pending.ShouldSatisfyAllConditions(r => r.Status.ShouldBe(RunStatus.PendingApproval), r => r.AgentId.ShouldBe(agent.Id));
            (await admin.Send(HttpMethod.Post, $"/api/runs/{pending.Id}/cancel")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }
        finally { await UpdateSettingsAsync(admin, new { requireRunApproval = false }); }
    }

    [Fact]
    public async Task Permissions_follow_azure_identity_project_roles_and_repository_rules()
    {
        var (admin, organizationId) = await AdminAsync();
        var project = await NewProjectAsync(admin, organizationId, "Perm", azure =>
        {
            Factory.CreateRemoteRepository(azure, "billing");
            azure.Members.Add(new AzureIdentity("Kim Lee", "kim@contoso.com"));
            azure.AddWorkItem(201, "Fix invoice rounding", "To Do", tags: "repo:billing");
        });

        // Only people of the organization's Azure teams can sign up, each with their own PAT.
        var outsider = await NewApi().Send(HttpMethod.Post, "/api/auth/signup", new { email = "olga@elsewhere.com", displayName = "Olga", password = Password, personalAccessToken = "olgaPersonalAccessToken000000" });
        outsider.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        Azure.TokenIdentities["janeTokenUsedByJoe0000000000"] = new AzureIdentity("Jane Doe", "jane@contoso.com");
        var foreign = await NewApi().Send(HttpMethod.Post, "/api/auth/signup", new { email = "kim@contoso.com", displayName = "Kim", password = Password, personalAccessToken = "janeTokenUsedByJoe0000000000" });
        foreign.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await foreign.Content.ReadAsStringAsync()).ShouldContain("belongs to jane@contoso.com");

        var joe = await DeveloperAsync("joe@contoso.com", "Joe Smith", organizationId);
        (await joe.Get<ProjectDto>($"/api/projects/{project.Id}")).MyPermissions.ShouldContain(Permission.RepositoryAgentWrite);
        (await joe.Send(HttpMethod.Get, "/api/settings/users")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var adminAgent = await admin.Post<AgentDto>("/api/agents", new
        {
            name = "Admin helper", kind = "Developer", runtime = "Framework", provider = "Anthropic", model = "claude-sonnet-4-5", enabled = true, maxConcurrentRuns = 1,
        });
        (await joe.Send(HttpMethod.Delete, $"/api/agents/{adminAgent.Id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await joe.Send(HttpMethod.Post, "/api/runs", new { projectId = project.Id, agentId = adminAgent.Id, workItemAzureId = 201 })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await admin.Send(HttpMethod.Delete, $"/api/agents/{adminAgent.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Repository rules: developers' agents may not write to billing, and Joe may not even see it.
        var repo = (await admin.Get<List<RepositoryDto>>($"/api/repos?project={project.Id}")).Single();
        await admin.Post<RepositoryAccessRuleDto>($"/api/settings/repositories/{repo.Id}/access-rules",
            new { role = ProjectRole.Developer, permission = Permission.RepositoryAgentWrite, effect = AccessEffect.Deny });
        await joe.Post<WorkItemSummaryDto>($"/api/projects/{project.Id}/work-items/201/assign", new { toMe = true });
        var run = await WaitForRunAsync(joe, r => r.ProjectId == project.Id && r.WorkItemAzureId == 201);
        run.Run.Status.ShouldBe(RunStatus.Failed);
        run.Run.Error.ShouldBe($"joe@contoso.com does not have '{Permission.RepositoryAgentWrite}' on billing");

        var hide = await admin.Post<RepositoryAccessRuleDto>($"/api/settings/repositories/{repo.Id}/access-rules",
            new { userId = (await joe.Get<MeDto>("/api/auth/me")).User.Id, permission = Permission.RepositoryView, effect = AccessEffect.Deny });
        (await joe.Get<List<RepositoryDto>>($"/api/repos?project={project.Id}")).ShouldBeEmpty();
        (await joe.Send(HttpMethod.Get, $"/api/repos/{repo.Id}/tree")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.Send(HttpMethod.Delete, $"/api/settings/repositories/{repo.Id}/access-rules/{hide.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await joe.Get<List<RepositoryDto>>($"/api/repos?project={project.Id}")).ShouldHaveSingleItem();

        // Project roles are managed in Settings → Users; removing the membership hides the project.
        var users = await admin.Get<UsersDto>("/api/settings/users");
        var joeRow = users.Users.Single(u => u.Email == "joe@contoso.com");
        joeRow.Projects.ShouldContain(p => p.ProjectId == project.Id && p.Source == MembershipSource.AzureTeam);
        joeRow.AzureConnected.ShouldBeTrue();
        (await admin.Send(HttpMethod.Put, $"/api/settings/users/{joeRow.UserId}/projects/{project.Id}", new { role = ProjectRole.Viewer })).EnsureSuccessStatusCode();
        (await joe.Send(HttpMethod.Post, $"/api/projects/{project.Id}/work-items/201/assign", new { toMe = true })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await admin.Send(HttpMethod.Delete, $"/api/settings/users/{joeRow.UserId}/projects/{project.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await joe.Send(HttpMethod.Get, $"/api/projects/{project.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Validation is enforced (FluentValidation through Wolverine middleware).
        (await admin.Send(HttpMethod.Post, $"/api/settings/repositories/{repo.Id}/access-rules", new { permission = "costs.view", effect = "Allow" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Projects_follow_the_azure_organization()
    {
        var (admin, organizationId) = await AdminAsync();
        var mobile = await NewProjectAsync(admin, organizationId, "Mobile App");
        mobile.Key.ShouldBe("MOBILEAP");
        Azure.RemoveProject("Mobile App");
        await NewProjectAsync(admin, organizationId, "Website");
        var projects = await admin.Get<List<ProjectDto>>("/api/projects");
        projects.ShouldNotContain(p => p.Id == mobile.Id);
        projects.ShouldContain(p => p.Name == "Website");
        (await admin.Get<SettingsDto>("/api/settings")).Azure.LastSyncError.ShouldBeNull();
    }

    [Fact]
    public async Task Budgets_stop_runs_and_reviewer_agents_post_findings()
    {
        var (admin, organizationId) = await AdminAsync();
        var project = await NewProjectAsync(admin, organizationId, "Cost", azure =>
        {
            Factory.CreateRemoteRepository(azure, "web");
            azure.AddWorkItem(301, "Dark mode", "Doing", tags: "repo:web");
        });
        var azure = Azure.Project("Cost");

        var budgeted = await admin.Post<AgentDto>("/api/agents", new
        {
            name = "Budgeted", kind = "Developer", runtime = "Framework", provider = "Anthropic", model = "claude-sonnet-4-5", projectId = project.Id, enabled = true,
            maxConcurrentRuns = 1, monthlyBudgetUsd = 0m,
        });
        var started = await admin.Post<JsonElement>("/api/runs", new { projectId = project.Id, agentId = budgeted.Id, workItemAzureId = 301 });
        var failed = await WaitForRunAsync(admin, r => r.Id == started.GetProperty("id").GetGuid());
        failed.Run.Status.ShouldBe(RunStatus.Failed);
        failed.Run.Error!.ShouldContain("budget for Budgeted is exhausted");
        failed.ModelCalls.ShouldBe(0);
        (await admin.Get<List<BudgetLine>>("/api/costs/budgets")).ShouldContain(b => b.Scope == "agent" && b.Name == "Budgeted" && b.LimitUsd == 0m);

        // A project reviewer reviews new human pull requests on sync.
        var reviewer = await admin.Post<AgentDto>("/api/agents", new
        {
            name = "Cost reviewer", kind = "Reviewer", runtime = "Framework", provider = "Anthropic", model = "claude-sonnet-4-5", projectId = project.Id, enabled = true,
            maxConcurrentRuns = 2,
        });
        reviewer.OwnerUserId.ShouldBeNull(); // shared
        azure.FileContents["/src/theme.ts"] = "export const dark = true;\nexport const contrast = 0.5;\n";
        lock (azure.PullRequests)
            azure.PullRequests.Add((new AzurePullRequest(50, "Theme switch", "active", "refs/heads/feature/theme", "refs/heads/main", "Ayşe", DateTimeOffset.UtcNow, null,
                "def456", "web"), 301));
        Factory.Models.Script.Enqueue(_ => ScriptedChatClients.Text("""
            {"verdict":"request_changes","summary":"Contrast is too low.","findings":[{"file":"/src/theme.ts","line":2,"severity":"major","message":"Contrast 0.5 fails WCAG AA."}]}
            """));
        (await SyncAsync(organizationId, project.Id)).QueuedRuns.ShouldBe(1);
        var review = await WaitForRunAsync(admin, r => r.Kind == RunKind.Review && r.AgentId == reviewer.Id);
        review.Run.Error.ShouldBeNull();
        review.Summary.ShouldBe("RequestChanges: Contrast is too low.");
        azure.Threads.ShouldContain(t => t.PullRequestId == 50 && t.File == "/src/theme.ts" && t.Line == 2 && t.Content.Contains("WCAG"));
        var pr = (await admin.Get<List<PullRequestDto>>($"/api/pull-requests?project={project.Id}")).Single();
        pr.ShouldSatisfyAllConditions(p => p.CreatedByAgent.ShouldBeFalse(), p => p.LastReviewVerdict.ShouldBe(ReviewVerdict.RequestChanges));
        (await admin.Get<PullRequestDetailDto>($"/api/pull-requests/{pr.Id}")).Reviews.Single().Findings.Single().Line.ShouldBe(2);
    }

    [Fact]
    public async Task Per_run_token_limit_stops_a_runaway_agent()
    {
        var (admin, organizationId) = await AdminAsync();
        var project = await NewProjectAsync(admin, organizationId, "Limit", azure =>
        {
            Factory.CreateRemoteRepository(azure, "ops");
            azure.AddWorkItem(501, "Rotate logs", "Doing", tags: "repo:ops");
        });
        var agent = await admin.Post<AgentDto>("/api/agents", new
        {
            name = "Limited", kind = "Developer", runtime = "Framework", provider = "Anthropic", model = "claude-sonnet-4-5", projectId = project.Id, enabled = true,
            maxConcurrentRuns = 1, settings = new { maxIterations = 40, maxTokensPerRun = 25_000, maxCostPerRunUsd = 5m, timeoutMinutes = 10 },
        });
        for (var i = 0; i < 5; i++) Factory.Models.Script.Enqueue(_ => ScriptedChatClients.ToolCall("list_files", new() { ["path"] = "ops" }));
        var started = await admin.Post<JsonElement>("/api/runs", new { projectId = project.Id, agentId = agent.Id, workItemAzureId = 501 });
        var run = await WaitForRunAsync(admin, r => r.Id == started.GetProperty("id").GetGuid());
        Factory.Models.Script.Clear();
        run.Run.Status.ShouldBe(RunStatus.Failed);
        run.Run.Error.ShouldBe("Token limit exceeded (33000 > 25000)");
        run.ModelCalls.ShouldBe(3);
        run.Run.CostUsd.ShouldBe(0.135m);
        Factory.Models.Created.ShouldContain(c => c.Provider == ModelProvider.Anthropic && c.ApiKey == "sk-ant-test");
    }

    [Fact]
    public async Task Service_hooks_require_the_organization_password_and_refresh_items()
    {
        var (admin, organizationId) = await AdminAsync();
        var project = await NewProjectAsync(admin, organizationId, "Hook");
        var password = (await admin.Post<JsonElement>("/api/settings/azure/service-hook-password", new { })).GetProperty("password").GetString()!;
        Azure.Project("Hook").AddWorkItem(401, "Hooked item", "To Do");

        var anonymous = NewApi();
        var payload = new { eventType = "workitem.updated", resource = new { workItemId = 401 }, resourceContainers = new { project = new { id = Azure.Project("Hook").AzureId } } };
        anonymous.Http.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String("azure:wrong"u8.ToArray()));
        (await anonymous.Send(HttpMethod.Post, $"/api/hooks/{organizationId}", payload)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        anonymous.Http.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"azure:{password}")));
        (await anonymous.Send(HttpMethod.Post, $"/api/hooks/{organizationId}", payload)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await Eventually(async () => (await admin.Get<List<WorkItemSummaryDto>>($"/api/projects/{project.Id}/work-items")).Any(w => w.AzureId == 401), "hooked work item");
        (await admin.Get<SettingsDto>("/api/settings")).Azure.ServiceHookPasswordConfigured.ShouldBeTrue();
    }
}
