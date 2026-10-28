using AzureAgent.Application.Features.Auth;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Application.Features.Projects;
using AzureAgent.Domain.Agents;
using AzureAgent.Domain.Common;
using AzureAgent.Domain.Costs;
using AzureAgent.Domain.Projects;
using AzureAgent.Infrastructure.AgentRuntime;
using Shouldly;

namespace AzureAgent.UnitTests;

public sealed class CostTests
{
    [Fact]
    public void Price_bills_cached_input_separately()
    {
        var price = new ModelPrice { Model = "claude-sonnet", InputPerMillion = 3m, OutputPerMillion = 15m, CachedInputPerMillion = 0.3m };
        price.Cost(1_000_000, 100_000, 0).ShouldBe(4.5m);
        // 400k of the 1M input tokens were cache hits.
        price.Cost(1_000_000, 0, 400_000).ShouldBe(1.8m + 0.12m);
    }

    [Fact]
    public void Run_accumulates_usage_and_follows_its_state_machine()
    {
        var run = AgentRun.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), RunKind.Implement, requiresApproval: true);
        run.Status.ShouldBe(RunStatus.PendingApproval);
        Should.Throw<DomainException>(() => run.Start(DateTimeOffset.UtcNow));
        run.Approve("lead@contoso.com");
        run.Start(DateTimeOffset.UtcNow);
        run.RecordUsage(1000, 200, 100, 0.01m);
        run.RecordUsage(500, 100, 0, 0.005m);
        run.InputTokens.ShouldBe(1500);
        run.CostUsd.ShouldBe(0.015m);
        run.ModelCalls.ShouldBe(2);
        Should.Throw<DomainException>(() => run.Cancel(DateTimeOffset.UtcNow));
        run.Succeed(DateTimeOffset.UtcNow, "done");
        run.IsActive.ShouldBeFalse();
        run.Fail(DateTimeOffset.UtcNow, "ignored after completion");
        run.Status.ShouldBe(RunStatus.Succeeded);
    }
}

public sealed class PermissionTests
{
    [Fact]
    public void Roles_grant_increasing_permissions()
    {
        RolePermissions.For(ProjectRole.Viewer).ShouldNotContain(Permission.WorkItemsAssign);
        RolePermissions.For(ProjectRole.Developer).ShouldContain(Permission.WorkItemsAssign);
        RolePermissions.For(ProjectRole.Developer).ShouldNotContain(Permission.AgentsManage);
        RolePermissions.For(ProjectRole.Reviewer).ShouldContain(Permission.PullRequestsReview);
        RolePermissions.For(ProjectRole.Maintainer).ShouldContain(Permission.AgentsManage);
        RolePermissions.For(ProjectRole.Maintainer).ShouldNotContain(Permission.SecretsManage);
        RolePermissions.For(ProjectRole.Admin).Count.ShouldBe(Permission.All.Count);
    }
}

public sealed class AzureMappingTests
{
    [Fact]
    public void Tags_name_repositories_and_messages_reference_work_items()
    {
        WorkItemMapping.RepositoryNames(WorkItemMapping.ParseTags("repo:api; backend; Repo:Web ; repo:api")).ShouldBe(["api", "Web"]);
        WorkItemMapping.WorkItemReferences("AB#12 fix, see ab#7 and AB#12").ShouldBe([12, 7]);
    }

    [Fact]
    public void Branch_names_are_safe_slugs()
    {
        WorkItemMapping.BranchName(42, "Çok önemli: Login düzelt!").ShouldBe("ab/42-cok-onemli-login-duzelt");
    }

    [Fact]
    public void Reconciliation_query_imports_open_items_and_history_first_then_only_changes()
    {
        var first = WorkItemMapping.ReconciliationQuery(null, ["Closed", "O'Brien"]);
        first.ShouldContain("@today - 365");
        first.ShouldContain("NOT IN ('Closed', 'O''Brien')");
        var next = WorkItemMapping.ReconciliationQuery(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero), ["Closed"]);
        next.ShouldContain("[System.ChangedDate] >= '2026-10-19'"); // a day of overlap absorbs clock skew
        next.ShouldNotContain("NOT IN");
    }

    [Fact]
    public void Html_descriptions_become_plain_text()
    {
        WorkItemMapping.HtmlToText("<p>Fix &amp; ship</p><ul><li>a</li></ul>").ShouldBe("Fix & ship\na");
    }
}

public sealed class ReviewParsingTests
{
    [Fact]
    public void Parses_review_json_even_inside_prose()
    {
        var review = AgentRunExecutor.ParseReview("""
            Here is my review:
            {"verdict":"request_changes","summary":"One bug.","findings":[{"file":"/a.cs","line":3,"severity":"major","message":"Null check"}]}
            """);
        review.Verdict.ShouldBe(ReviewVerdict.RequestChanges);
        review.Findings.Single().Line.ShouldBe(3);
        AgentRunExecutor.ParseReview("Looks fine to me").Verdict.ShouldBe(ReviewVerdict.Comment);
    }
}

public sealed class WorkspaceSecurityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ws-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Paths_cannot_escape_the_workspace_or_reach_git_metadata()
    {
        var tools = new WorkspaceTools(_root, new HashSet<string> { "dotnet" }, new ProcessRunner(Microsoft.Extensions.Options.Options.Create(new AgentRuntimeOptions())), (_, _) => { });
        Should.Throw<InvalidOperationException>(() => tools.Resolve("../etc/passwd"));
        Should.Throw<InvalidOperationException>(() => tools.Resolve("/../../x"));
        Should.Throw<InvalidOperationException>(() => tools.Resolve("api/.git/config"));
        tools.WriteFile("api/src/A.cs", "class A {}");
        tools.ReadFile("api/src/A.cs").ShouldBe("1: class A {}");
        tools.ReplaceInFile("api/src/A.cs", "class A", "sealed class A").ShouldStartWith("Replaced");
        tools.Search("sealed", "api").ShouldContain("A.cs:1");
    }

    [Fact]
    public async Task Only_allow_listed_commands_run_and_git_is_reserved()
    {
        Directory.CreateDirectory(Path.Combine(_root, "api"));
        var tools = new WorkspaceTools(_root, new HashSet<string> { "echo", "git" }, new ProcessRunner(Microsoft.Extensions.Options.Options.Create(new AgentRuntimeOptions())), (_, _) => { });
        (await tools.RunCommand("api", "rm", ["-rf", "/"])).ShouldStartWith("Command 'rm' is not allowed");
        (await tools.RunCommand("api", "git", ["push"])).ShouldBe("Use of git is reserved for the platform");
        (await tools.RunCommand("api", "echo", ["hello"])).ShouldContain("hello");
    }

    [Fact]
    public void Git_auth_travels_in_environment_scoped_to_the_azure_origin()
    {
        var env = GitWorkspace.BuildEnvironment("https://dev.azure.com/contoso", Application.Abstractions.AzureCredential.Pat("secret-pat"));
        env["GIT_CONFIG_KEY_0"].ShouldBe("http.https://dev.azure.com/.extraHeader");
        env["GIT_CONFIG_VALUE_0"].ShouldStartWith("Authorization: Basic ");
        env.Values.ShouldNotContain(v => v.Contains("secret-pat"));
    }
}

public sealed class ValidationTests
{
    [Fact]
    public void Passwords_must_be_strong()
    {
        new SetupCommandValidator().Validate(new SetupCommand("a@b.co", "A", "weakpass", "https://dev.azure.com/contoso", "abcdefghijklmnopqrstuvwxyz0123")).Errors
            .Select(e => e.ErrorMessage).ShouldContain("Password must contain an uppercase letter");
        new SetupCommandValidator().Validate(new SetupCommand("a@b.co", "A", "Str0ngPassword", "https://dev.azure.com/contoso", "abcdefghijklmnopqrstuvwxyz0123")).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Setup_requires_an_azure_devops_organization_url_and_a_pat()
    {
        var result = new SetupCommandValidator().Validate(new SetupCommand("a@b.co", "A", "Str0ngPassword", "https://github.com/x", "not a pat!"));
        result.Errors.Select(e => e.PropertyName).Distinct().ShouldBe(["AzureOrganizationUrl", "PersonalAccessToken"], ignoreOrder: true);
        new AzureUrlValidator().Validate("https://contoso.visualstudio.com").IsValid.ShouldBeTrue();
        new AzureUrlValidator().Validate("https://dev.azure.com/contoso/Project").IsValid.ShouldBeFalse();
    }
}
