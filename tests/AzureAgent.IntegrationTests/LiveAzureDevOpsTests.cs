using AzureAgent.Application.Abstractions;
using AzureAgent.Infrastructure.AzureDevOps;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace AzureAgent.IntegrationTests;

/// <summary>
/// Read-only checks of the REST client against a real Azure DevOps project. Opt-in: set
/// AZURE_DEVOPS_LIVE_PAT, AZURE_DEVOPS_LIVE_ORG (https://dev.azure.com/org) and AZURE_DEVOPS_LIVE_PROJECT.
/// </summary>
public sealed class LiveAzureDevOpsTests
{
    private static readonly string? Pat = Environment.GetEnvironmentVariable("AZURE_DEVOPS_LIVE_PAT");
    private static readonly string Organization = Environment.GetEnvironmentVariable("AZURE_DEVOPS_LIVE_ORG") ?? "";
    private static readonly string Project = Environment.GetEnvironmentVariable("AZURE_DEVOPS_LIVE_PROJECT") ?? "";

    private static AzureDevOpsClient Client()
    {
        if (string.IsNullOrEmpty(Pat)) Assert.Skip("AZURE_DEVOPS_LIVE_PAT is not set");
        return new AzureDevOpsClient(new HttpClient(), new AzureConnectionInfo(Organization, Project, AzureCredential.Pat(Pat!)), NullLogger<AzureDevOpsClient>.Instance);
    }

    [Fact]
    public async Task Reads_identity_teams_repositories_and_process()
    {
        var azure = Client();
        var ct = TestContext.Current.CancellationToken;
        (await azure.GetProjectNameAsync(ct)).ShouldBe(Project);
        var me = await azure.GetAuthenticatedIdentityAsync(ct);
        me.UniqueName.ShouldContain("@");

        var teams = await azure.ListTeamsAsync(ct);
        teams.ShouldNotBeEmpty();
        var members = await azure.ListTeamMembersAsync(teams[0].Id, ct);
        members.ShouldContain(m => m.UniqueName.Equals(me.UniqueName, StringComparison.OrdinalIgnoreCase));
        (await azure.ListTeamAreasAsync(teams[0].Id, ct)).ShouldNotBeEmpty();

        (await azure.ListRepositoriesAsync(ct)).ShouldNotBeEmpty();
        var types = await azure.ListWorkItemTypesAsync(ct);
        types.ShouldContain(t => t.States.Count > 0 && t.Fields.Contains("System.Title"));
        var ids = await azure.QueryWorkItemIdsAsync("SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project", ct);
        if (ids.Count > 0) (await azure.GetWorkItemsAsync(ids.Take(5).ToList(), ct)).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Rejected_credentials_fail_instead_of_returning_the_sign_in_page()
    {
        if (string.IsNullOrEmpty(Pat)) Assert.Skip("AZURE_DEVOPS_LIVE_PAT is not set");
        var azure = new AzureDevOpsClient(new HttpClient(), new AzureConnectionInfo(Organization, Project, AzureCredential.Pat("invalidtoken0000000000000000000000000000000000000000")),
            NullLogger<AzureDevOpsClient>.Instance);
        await Should.ThrowAsync<AzureDevOpsException>(() => azure.GetAuthenticatedIdentityAsync(TestContext.Current.CancellationToken));
    }
}
