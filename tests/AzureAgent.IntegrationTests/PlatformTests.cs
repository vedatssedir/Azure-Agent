using System.Net;
using System.Text.Json;
using AzureAgent.Application.Features.Agents;
using AzureAgent.Application.Features.Auth;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Application.Features.Costs;
using AzureAgent.Application.Features.Organizations;
using AzureAgent.Application.Features.Projects;
using AzureAgent.Application.Features.Runs;
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
    public async Task Developer_assigns_work_item_and_agent_delivers_a_pull_request_with_tracked_cost()
    {
        var (admin, organizationId) = await AdminAsync();
        var project = await CreateProjectAsync(admin, "FLOW");
        var azure = Factory.Azure.Project("FLOW");
        var bare = Factory.CreateRemoteRepository(azure, "api");
        azure.AddWorkItem(101, "Add health endpoint", "New", tags: "repo:api; backend");
        azure.AddWorkItem(102, "Old bug", "Closed", assignee: "jane@contoso.com", tags: "repo:api");

        var sync = await admin.Post<SyncResult>($"/api/projects/{project.Id}/azure/sync", new { });
        (sync.Teams, sync.Repositories, sync.WorkItems, sync.Commits, sync.PullRequests, sync.QueuedRuns).ShouldBe((1, 1, 2, 0, 0, 0));

        // One developer agent per Azure team member, with a skill.
        await admin.Put<SkillDto>("/api/skills/dotnet", new { name = ".NET", content = "Always run dotnet test." });
        var team = (await admin.Get<List<TeamDto>>($"/api/projects/{project.Id}/teams")).Single();
        team.Members.Select(m => m.UniqueName).ShouldBe(["jane@contoso.com", "joe@contoso.com"], ignoreOrder: true);
        var provision = await admin.Post<JsonElement>($"/api/projects/{project.Id}/teams/{team.Id}/provision-agents",
            new { provider = "Anthropic", model = "claude-sonnet-4-5", skillKeys = new[] { "dotnet" } });
        provision.GetProperty("created").GetInt32().ShouldBe(2);
        (await admin.Post<JsonElement>($"/api/projects/{project.Id}/teams/{team.Id}/provision-agents", new { provider = "Anthropic", model = "claude-sonnet-4-5" }))
            .GetProperty("created").GetInt32().ShouldBe(0);
        var agents = await admin.Get<List<AgentDto>>($"/api/projects/{project.Id}/agents");
        var janeAgent = agents.Single(a => a.PairedUniqueName == "jane@contoso.com");
        janeAgent.Skills.ShouldBe(["dotnet"]);
        // The closed item assigned to Jane must not be dispatched.
        (await admin.Get<List<RunSummaryDto>>($"/api/projects/{project.Id}/runs")).ShouldBeEmpty();

        // Jane is a developer on the project and assigns the item to herself.
        var jane = await AddUserAsync(admin, "jane@contoso.com", "Jane Doe");
        await admin.Put<ProjectMemberDto>($"/api/projects/{project.Id}/members/{jane.UserId}", new { role = ProjectRole.Developer, azureUniqueName = "jane@contoso.com" });
        var janeApi = await LoginAsync("jane@contoso.com", organizationId);

        Factory.Models.Script.Enqueue(_ => ScriptedChatClients.ToolCall("write_file", new() { ["path"] = "api/Health.cs", ["content"] = "public static class Health { public const string Status = \"ok\"; }\n" }));
        Factory.Models.Script.Enqueue(_ => ScriptedChatClients.Text("Added Health.cs with a status constant. Tests: n/a."));

        var assigned = await janeApi.Post<WorkItemSummaryDto>($"/api/projects/{project.Id}/work-items/101/assign", new { toMe = true });
        assigned.AssignedToUniqueName.ShouldBe("jane@contoso.com");

        var run = await WaitForRunAsync(admin, project.Id, r => r.WorkItemAzureId == 101);
        run.Run.Error.ShouldBeNull();
        run.Run.Status.ShouldBe(RunStatus.Succeeded);
        run.Run.Branch.ShouldBe("ab/101-add-health-endpoint");
        run.Run.PullRequestUrls.ShouldBe(["https://dev.azure.com/contoso/Shop/_git/api/pullrequest/1"]);
        run.ModelCalls.ShouldBe(2);
        run.ToolCalls.ShouldBe(1);
        run.Run.InputTokens.ShouldBe(20_000);
        // claude-sonnet: 3 USD / 1M input, 15 USD / 1M output → 2 × (0.03 + 0.015)
        run.Run.CostUsd.ShouldBe(0.09m);
        run.Steps.Count(s => s.Type == RunStepType.ModelCall).ShouldBe(2);
        run.Steps.ShouldContain(s => s.Type == RunStepType.Git && s.Title == "Opened pull request in api");

        // Delivered to Azure: branch pushed, PR linked to the work item, comment, review state.
        AppFactory.Git(Factory.Root, "--git-dir", bare, "log", "--oneline", "ab/101-add-health-endpoint").ShouldContain("AB#101: Add health endpoint");
        azure.PullRequests.Single().WorkItemId.ShouldBe(101);
        azure.Comments.ShouldContain(c => c.WorkItemId == 101 && c.Html.Contains("pullrequest/1"));
        azure.WorkItems[101].Fields["System.State"]!.ToString().ShouldBe("Resolved");
        Factory.Models.Created.ShouldContain(c => c.Provider == ModelProvider.Anthropic && c.ApiKey == "sk-ant-test");

        // Work item view links run, PR and cost; cost-per-job analysis.
        var detail = await janeApi.Get<WorkItemDetailDto>($"/api/projects/{project.Id}/work-items/101");
        detail.Runs.Single().Status.ShouldBe(RunStatus.Succeeded);
        detail.PullRequests.Single().CreatedByAgent.ShouldBeTrue();
        detail.Summary.CostUsd.ShouldBe(0.09m);
        var byWorkItem = await admin.Get<CostReport>($"/api/projects/{project.Id}/costs?groupBy=WorkItem");
        byWorkItem.TotalCostUsd.ShouldBe(0.09m);
        byWorkItem.CostPerSucceededRunUsd.ShouldBe(0.09m);
        byWorkItem.Rows.Single().Label.ShouldBe("#101 Add health endpoint");
        var byAgent = await admin.Get<CostReport>($"/api/projects/{project.Id}/costs?groupBy=Agent");
        byAgent.Rows.Single().Label.ShouldBe(janeAgent.Name);

        // Re-syncing does not dispatch the delivered item again.
        (await admin.Post<SyncResult>($"/api/projects/{project.Id}/azure/sync", new { })).QueuedRuns.ShouldBe(0);
    }

    [Fact]
    public async Task Permissions_follow_project_roles_and_repository_rules()
    {
        var (admin, organizationId) = await AdminAsync();
        var project = await CreateProjectAsync(admin, "PERM");
        var azure = Factory.Azure.Project("PERM");
        Factory.CreateRemoteRepository(azure, "billing");
        azure.AddWorkItem(201, "Fix invoice rounding", "Active", tags: "repo:billing");
        await admin.Post<SyncResult>($"/api/projects/{project.Id}/azure/sync", new { });

        var dev = await AddUserAsync(admin, "joe@contoso.com", "Joe Smith");
        await admin.Put<ProjectMemberDto>($"/api/projects/{project.Id}/members/{dev.UserId}", new { role = ProjectRole.Developer, azureUniqueName = "joe@contoso.com" });
        var viewer = await AddUserAsync(admin, "viewer@contoso.com", "Vic Viewer");
        await admin.Put<ProjectMemberDto>($"/api/projects/{project.Id}/members/{viewer.UserId}", new { role = ProjectRole.Viewer });
        var outsider = await AddUserAsync(admin, "outsider@contoso.com", "Olga Outsider");
        var devApi = await LoginAsync("joe@contoso.com", organizationId);
        var viewerApi = await LoginAsync("viewer@contoso.com", organizationId);
        var outsiderApi = await LoginAsync("outsider@contoso.com", organizationId);

        // Role checks.
        (await devApi.Send(HttpMethod.Post, $"/api/projects/{project.Id}/agents", new { name = "x", kind = "Developer", provider = "OpenAI", model = "gpt-5", enabled = true, maxConcurrentRuns = 1 }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await devApi.Send(HttpMethod.Get, $"/api/projects/{project.Id}/costs")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await devApi.Send(HttpMethod.Get, $"/api/projects/{project.Id}/secrets")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewerApi.Send(HttpMethod.Post, $"/api/projects/{project.Id}/work-items/201/assign", new { toMe = true })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viewerApi.Send(HttpMethod.Get, $"/api/projects/{project.Id}/work-items")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await outsiderApi.Send(HttpMethod.Get, $"/api/projects/{project.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await outsiderApi.Get<List<ProjectDto>>("/api/projects")).ShouldNotContain(p => p.Id == project.Id);
        (await devApi.Get<ProjectDto>($"/api/projects/{project.Id}")).MyPermissions.ShouldContain(Permission.RepositoryAgentWrite);

        // Repository rule: developers' agents may not write to billing.
        await admin.Post<AgentDto>($"/api/projects/{project.Id}/agents", new
        {
            name = "Joe (agent)", kind = "Developer", provider = "Anthropic", model = "claude-sonnet-4-5", pairedUniqueName = "joe@contoso.com", enabled = true, maxConcurrentRuns = 1,
        });
        var repo = (await admin.Get<List<RepositoryDto>>($"/api/projects/{project.Id}/repositories")).Single(r => r.Name == "billing");
        await admin.Post<RepositoryAccessRuleDto>($"/api/projects/{project.Id}/repositories/{repo.Id}/access-rules",
            new { role = ProjectRole.Developer, permission = Permission.RepositoryAgentWrite, effect = AccessEffect.Deny });
        (await devApi.Get<List<RepositoryDto>>($"/api/projects/{project.Id}/repositories")).ShouldContain(r => r.Name == "billing");

        await devApi.Post<WorkItemSummaryDto>($"/api/projects/{project.Id}/work-items/201/assign", new { toMe = true });
        var run = await WaitForRunAsync(admin, project.Id, r => r.WorkItemAzureId == 201);
        run.Run.Status.ShouldBe(RunStatus.Failed);
        run.Run.Error.ShouldBe($"joe@contoso.com does not have '{Permission.RepositoryAgentWrite}' on billing");

        // A view deny hides the repository from the developer.
        await admin.Post<RepositoryAccessRuleDto>($"/api/projects/{project.Id}/repositories/{repo.Id}/access-rules",
            new { userId = dev.UserId, permission = Permission.RepositoryView, effect = AccessEffect.Deny });
        (await devApi.Get<List<RepositoryDto>>($"/api/projects/{project.Id}/repositories")).ShouldNotContain(r => r.Name == "billing");

        // Validation is enforced (FluentValidation through Wolverine middleware).
        var invalid = await admin.Send(HttpMethod.Post, $"/api/projects/{project.Id}/repositories/{repo.Id}/access-rules", new { permission = "costs.view", effect = "Allow" });
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Organizations_are_isolated()
    {
        var (admin, organizationId) = await AdminAsync();
        var project = await CreateProjectAsync(admin, "ISO");
        var azure = Factory.Azure.Project("ISO");
        var other = await admin.Post<OrganizationMembershipDto>("/api/organizations", new { name = "Globex" });
        var member = await AddUserAsync(admin, "member@contoso.com", "Mia Member");
        var memberApi = await LoginAsync("member@contoso.com", other.OrganizationId);
        (await memberApi.Send(HttpMethod.Get, "/api/projects")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var globexAdmin = NewApi();
        globexAdmin.Authenticate(AdminToken, other.OrganizationId);
        (await globexAdmin.Get<List<ProjectDto>>("/api/projects")).ShouldBeEmpty();
        (await globexAdmin.Send(HttpMethod.Get, $"/api/projects/{project.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await globexAdmin.Get<List<OrganizationMemberDto>>("/api/organizations/members")).ShouldNotContain(m => m.UserId == member.UserId);
        organizationId.ShouldNotBe(other.OrganizationId);
    }

    [Fact]
    public async Task Budgets_stop_runs_and_reviewer_agents_post_findings()
    {
        var (admin, _) = await AdminAsync();
        var project = await CreateProjectAsync(admin, "COST");
        var azure = Factory.Azure.Project("COST");
        Factory.CreateRemoteRepository(azure, "web");
        azure.AddWorkItem(301, "Dark mode", "Active", tags: "repo:web");
        await admin.Post<SyncResult>($"/api/projects/{project.Id}/azure/sync", new { });

        // An exhausted agent budget fails the run before any model call.
        var agent = await admin.Post<AgentDto>($"/api/projects/{project.Id}/agents", new
        {
            name = "Budgeted", kind = "Developer", provider = "Anthropic", model = "claude-sonnet-4-5", enabled = true, maxConcurrentRuns = 1, monthlyBudgetUsd = 0m,
        });
        var started = await admin.Post<JsonElement>($"/api/projects/{project.Id}/runs", new { agentId = agent.Id, workItemAzureId = 301 });
        var runId = started.GetProperty("id").GetGuid();
        var failed = await WaitForRunAsync(admin, project.Id, r => r.Id == runId);
        failed.Run.Status.ShouldBe(RunStatus.Failed);
        failed.Run.Error!.ShouldContain("budget for Budgeted is exhausted");
        failed.ModelCalls.ShouldBe(0);
        (await admin.Get<List<BudgetLine>>($"/api/projects/{project.Id}/budgets")).ShouldContain(b => b.Scope == "agent" && b.Name == "Budgeted" && b.LimitUsd == 0m);

        // Reviewer agent: a new human PR is reviewed automatically on sync.
        await admin.Post<AgentDto>($"/api/projects/{project.Id}/agents", new
        {
            name = "Reviewer", kind = "Reviewer", provider = "Anthropic", model = "claude-sonnet-4-5", enabled = true, maxConcurrentRuns = 2,
        });
        azure.FileContents["/src/theme.ts"] = "export const dark = true;\nexport const contrast = 0.5;\n";
        azure.PullRequests.Add((new Application.Abstractions.AzurePullRequest(50, "Theme switch", "active", "refs/heads/feature/theme", "refs/heads/main",
            "Ayşe", DateTimeOffset.UtcNow, null, "def456", "web"), 301));
        Factory.Models.Script.Enqueue(_ => ScriptedChatClients.Text("""
            {"verdict":"request_changes","summary":"Contrast is too low.","findings":[{"file":"/src/theme.ts","line":2,"severity":"major","message":"Contrast 0.5 fails WCAG AA."}]}
            """));
        await admin.Post<SyncResult>($"/api/projects/{project.Id}/azure/sync", new { });
        var review = await WaitForRunAsync(admin, project.Id, r => r.Kind == RunKind.Review && r.AgentName == "Reviewer");
        review.Run.Error.ShouldBeNull();
        review.Run.Status.ShouldBe(RunStatus.Succeeded);
        review.Summary.ShouldBe("RequestChanges: Contrast is too low.");
        azure.Threads.ShouldContain(t => t.PullRequestId == 50 && t.File == "/src/theme.ts" && t.Line == 2 && t.Content.Contains("WCAG"));
        azure.Threads.ShouldContain(t => t.PullRequestId == 50 && t.File == null && t.Content.Contains("AI review"));
        var prs = await admin.Get<List<PullRequestDto>>($"/api/projects/{project.Id}/pull-requests");
        prs.ShouldContain(p => p.AzureId == 50 && !p.CreatedByAgent);
    }

    [Fact]
    public async Task Per_run_token_limit_stops_a_runaway_agent()
    {
        var (admin, _) = await AdminAsync();
        var project = await CreateProjectAsync(admin, "LIMIT");
        var azure = Factory.Azure.Project("LIMIT");
        Factory.CreateRemoteRepository(azure, "ops");
        azure.AddWorkItem(501, "Rotate logs", "Active", tags: "repo:ops");
        await admin.Post<SyncResult>($"/api/projects/{project.Id}/azure/sync", new { });
        var agent = await admin.Post<AgentDto>($"/api/projects/{project.Id}/agents", new
        {
            name = "Limited", kind = "Developer", provider = "Anthropic", model = "claude-sonnet-4-5", enabled = true, maxConcurrentRuns = 1,
            settings = new { maxIterations = 40, maxTokensPerRun = 25_000, maxCostPerRunUsd = 5m, timeoutMinutes = 10 },
        });
        // The model keeps asking for tools; the third call crosses 25k tokens.
        for (var i = 0; i < 5; i++) Factory.Models.Script.Enqueue(_ => ScriptedChatClients.ToolCall("list_files", new() { ["path"] = "ops" }));
        var started = await admin.Post<JsonElement>($"/api/projects/{project.Id}/runs", new { agentId = agent.Id, workItemAzureId = 501 });
        var run = await WaitForRunAsync(admin, project.Id, r => r.Id == started.GetProperty("id").GetGuid());
        Factory.Models.Script.Clear();
        run.Run.Status.ShouldBe(RunStatus.Failed);
        run.Run.Error.ShouldBe("Token limit exceeded (33000 > 25000)");
        run.ModelCalls.ShouldBe(3);
        run.Run.CostUsd.ShouldBe(0.135m);
    }

    [Fact]
    public async Task Service_hooks_require_the_project_password_and_refresh_items()
    {
        var (admin, _) = await AdminAsync();
        var project = await CreateProjectAsync(admin, "HOOK");
        var azure = Factory.Azure.Project("HOOK");
        (await admin.Send(HttpMethod.Put, $"/api/projects/{project.Id}/secrets/AZURE_SERVICE_HOOK_PASSWORD", new { value = "hook-password-123" })).EnsureSuccessStatusCode();
        azure.AddWorkItem(401, "Hooked item", "New");

        var anonymous = NewApi();
        var payload = new { eventType = "workitem.updated", resource = new { workItemId = 401 } };
        anonymous.Http.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String("azure:wrong"u8.ToArray()));
        (await anonymous.Send(HttpMethod.Post, $"/api/hooks/{project.Id}", payload)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        anonymous.Http.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String("azure:hook-password-123"u8.ToArray()));
        (await anonymous.Send(HttpMethod.Post, $"/api/hooks/{project.Id}", payload)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        for (var i = 0; i < 50; i++)
        {
            var items = await admin.Get<List<WorkItemSummaryDto>>($"/api/projects/{project.Id}/work-items");
            if (items.Any(w => w.AzureId == 401)) return;
            await Task.Delay(200);
        }
        throw new TimeoutException("Hook did not sync the work item");
    }
}
