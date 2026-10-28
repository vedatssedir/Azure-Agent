using System.Net;
using System.Text.Json;
using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Features.Auth;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Application.Features.Identity;
using AzureAgent.Application.Features.Projects;
using AzureAgent.Domain.Identity;
using AzureAgent.Domain.Projects;
using Shouldly;

namespace AzureAgent.IntegrationTests;

/// <summary>Azure team sync → people, invitations, personal Azure credentials, Entra ID sign-in, service principal.</summary>
[Collection(PlatformCollection.Name)]
public sealed class IdentityTests(AppFactory factory) : PlatformTestBase(factory)
{
    private static object Settings(ProjectDto project, object? overrides = null)
    {
        var settings = JsonSerializer.SerializeToNode(project.Settings, AppFactory.Json)!.AsObject();
        if (overrides is not null)
            foreach (var (key, value) in JsonSerializer.SerializeToNode(overrides, AppFactory.Json)!.AsObject()) settings[key] = value?.DeepClone();
        return new { name = project.Name, project.Description, project.AzureOrganizationUrl, project.AzureProjectName, project.MonthlyBudgetUsd, settings };
    }

    [Fact]
    public async Task Azure_team_members_become_project_members_with_mapped_roles()
    {
        var (admin, organizationId) = await AdminAsync();
        var project = await CreateProjectAsync(admin, "TEAM");
        var azure = Factory.Azure.Project("TEAM");
        azure.Teams.Clear();
        azure.Teams.AddRange([new AzureTeam("t-backend", "Backend"), new AzureTeam("t-qa", "QA")]);
        azure.TeamMembers["t-backend"] = [new("Ali Kaya", "Ali@Contoso.com"), new("Bea Demir", "bea@contoso.com"), new("Zed Yilmaz", "zed@contoso.com")];
        azure.TeamMembers["t-qa"] = [new("Quinn QA", "qa@contoso.com"), new("Bea Demir", "bea@contoso.com")];
        azure.TeamAreas["t-backend"] = [("TEAM\\Backend", true)];
        azure.TeamAreas["t-qa"] = [("TEAM\\QA", false)];
        azure.AddWorkItem(601, "Backend child area item", "New", areaPath: "TEAM\\Backend\\Api");
        azure.AddWorkItem(602, "QA item", "New", areaPath: "TEAM\\QA", assignee: "qa@contoso.com");
        azure.AddWorkItem(603, "Root item", "New", areaPath: "TEAM");

        var ali = await AddUserAsync(admin, "ali@contoso.com", "Ali Kaya");
        var qa = await AddUserAsync(admin, "qa@contoso.com", "Quinn QA");
        project = await admin.Put<ProjectDto>($"/api/projects/{project.Id}", Settings(project, new
        {
            teamRoles = new[] { new { team = "Backend", role = "Developer" }, new { team = "QA", role = "Reviewer" } },
            autoInviteTeamMembers = true,
        }));

        var sync = await admin.Post<SyncResult>($"/api/projects/{project.Id}/azure/sync", new { });
        sync.MembersAdded.ShouldBe(2);
        sync.Invited.ShouldBe(2); // bea and zed have no account yet

        var members = await admin.Get<List<ProjectMemberDto>>($"/api/projects/{project.Id}/members");
        members.Single(m => m.UserId == ali.UserId).ShouldSatisfyAllConditions(
            m => m.Role.ShouldBe(ProjectRole.Developer), m => m.Source.ShouldBe(MembershipSource.AzureTeam), m => m.AzureUniqueName.ShouldBe("ali@contoso.com"));
        members.Single(m => m.UserId == qa.UserId).Role.ShouldBe(ProjectRole.Reviewer);

        var people = await admin.Get<List<ProjectPersonDto>>($"/api/projects/{project.Id}/people");
        people.Select(p => p.UniqueName).ShouldBe(["ali@contoso.com", "bea@contoso.com", "qa@contoso.com", "zed@contoso.com"], ignoreOrder: true);
        var bea = people.Single(p => p.UniqueName == "bea@contoso.com");
        bea.Teams.ShouldBe(["Backend", "QA"], ignoreOrder: true);
        bea.MappedRole.ShouldBe(ProjectRole.Reviewer); // highest mapped role wins
        bea.UserId.ShouldBeNull();
        bea.InvitationStatus.ShouldBe("Pending");
        people.Single(p => p.UniqueName == "ali@contoso.com").Role.ShouldBe(ProjectRole.Developer);

        // The team board follows the team's area paths (children included only where the team says so).
        var teams = await admin.Get<List<TeamDto>>($"/api/projects/{project.Id}/teams");
        var backend = teams.Single(t => t.Name == "Backend");
        backend.AreaPaths.ShouldBe(["TEAM\\Backend (+)"]);
        (await admin.Get<List<WorkItemSummaryDto>>($"/api/projects/{project.Id}/work-items?team={backend.Id}")).Select(w => w.AzureId).ShouldBe([601]);
        (await admin.Get<List<WorkItemSummaryDto>>($"/api/projects/{project.Id}/work-items?team={teams.Single(t => t.Name == "QA").Id}"))
            .Select(w => w.AzureId).ShouldBe([602]);
        (await admin.Get<List<WorkItemSummaryDto>>($"/api/projects/{project.Id}/work-items?unassigned=true")).Select(w => w.AzureId)
            .ShouldBe([601, 603], ignoreOrder: true);

        // Leaving the team removes a synced membership; a manually pinned one stays.
        await admin.Put<ProjectMemberDto>($"/api/projects/{project.Id}/members/{qa.UserId}", new { role = ProjectRole.Maintainer, azureUniqueName = "qa@contoso.com" });
        azure.TeamMembers["t-backend"] = [new("Bea Demir", "bea@contoso.com")];
        azure.TeamMembers["t-qa"] = [];
        var resync = await admin.Post<SyncResult>($"/api/projects/{project.Id}/azure/sync", new { });
        resync.MembersRemoved.ShouldBe(1);
        members = await admin.Get<List<ProjectMemberDto>>($"/api/projects/{project.Id}/members");
        members.ShouldNotContain(m => m.UserId == ali.UserId);
        members.Single(m => m.UserId == qa.UserId).ShouldSatisfyAllConditions(m => m.Role.ShouldBe(ProjectRole.Maintainer), m => m.Source.ShouldBe(MembershipSource.Manual));

        // Ali can still sign in but no longer sees the project.
        var aliApi = await LoginAsync("ali@contoso.com", organizationId);
        (await aliApi.Send(HttpMethod.Get, $"/api/projects/{project.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Invited_team_member_accepts_and_lands_in_the_project()
    {
        var (admin, organizationId) = await AdminAsync();
        var project = await CreateProjectAsync(admin, "INV");
        var azure = Factory.Azure.Project("INV");
        azure.Teams.Clear();
        azure.Teams.Add(new AzureTeam("t-web", "Web"));
        azure.TeamMembers["t-web"] = [new("Nur Aksoy", "nur@contoso.com"), new("Mert Ak", "mert@contoso.com")];
        await admin.Post<SyncResult>($"/api/projects/{project.Id}/azure/sync", new { });

        // A maintainer may invite people of the project's Azure teams, and nobody else.
        var maintainer = await AddUserAsync(admin, "maint@contoso.com", "Mai Maintainer");
        await admin.Put<ProjectMemberDto>($"/api/projects/{project.Id}/members/{maintainer.UserId}", new { role = ProjectRole.Admin });
        var maintainerApi = await LoginAsync("maint@contoso.com", organizationId);
        (await maintainerApi.Send(HttpMethod.Post, $"/api/projects/{project.Id}/people/invite",
            new { people = new[] { new { email = "stranger@evil.test", displayName = "Stranger" } } })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var links = await maintainerApi.Post<List<InvitationLinkDto>>($"/api/projects/{project.Id}/people/invite",
            new { people = new[] { new { email = "nur@contoso.com", displayName = "Nur Aksoy" } } });
        var link = links.Single();
        link.Url.ShouldStartWith("https://agent.test/invite/");
        link.EmailSent.ShouldBeFalse();
        // Inviting again while pending is a no-op.
        (await maintainerApi.Post<List<InvitationLinkDto>>($"/api/projects/{project.Id}/people/invite",
            new { people = new[] { new { email = "nur@contoso.com", displayName = "Nur Aksoy" } } })).ShouldBeEmpty();

        var token = Uri.UnescapeDataString(link.Url.Split("/invite/")[1]);
        var info = await NewApi().Get<InvitationInfoDto>($"/api/auth/invitations/{token}");
        info.ShouldSatisfyAllConditions(i => i.Email.ShouldBe("nur@contoso.com"), i => i.OrganizationName.ShouldBe("Contoso"), i => i.HasAccount.ShouldBeFalse());

        (await NewApi().Send(HttpMethod.Post, "/api/auth/invitations/accept", new { token, password = "weak" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var auth = await NewApi().Post<AuthResult>("/api/auth/invitations/accept", new { token, displayName = "Nur A.", password = Password });
        auth.User.DisplayName.ShouldBe("Nur A.");
        (await NewApi().Send(HttpMethod.Post, "/api/auth/invitations/accept", new { token, password = Password })).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var nur = NewApi();
        nur.Authenticate(auth.AccessToken, organizationId);
        (await nur.Get<MeDto>("/api/auth/me")).Organizations.ShouldContain(o => o.OrganizationId == organizationId && o.Role == OrganizationRole.Member);
        var mine = await nur.Get<ProjectDto>($"/api/projects/{project.Id}");
        mine.MyPermissions.ShouldContain(Permission.WorkItemsAssign);
        (await admin.Get<List<ProjectPersonDto>>($"/api/projects/{project.Id}/people")).Single(p => p.UniqueName == "nur@contoso.com")
            .ShouldSatisfyAllConditions(p => p.InvitationStatus.ShouldBe("Accepted"), p => p.Role.ShouldBe(ProjectRole.Developer));

        // Organization admins see, renew and revoke invitations.
        var mert = (await admin.Post<List<InvitationLinkDto>>("/api/organizations/invitations",
            new { people = new[] { new { email = "mert@contoso.com", displayName = "Mert Ak" } }, role = "Member" })).Single();
        var renewed = await admin.Post<InvitationLinkDto>($"/api/organizations/invitations/{mert.Id}/renew", new { });
        renewed.Url.ShouldNotBe(mert.Url);
        (await NewApi().Send(HttpMethod.Get, $"/api/auth/invitations/{Uri.UnescapeDataString(mert.Url.Split("/invite/")[1])}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.Send(HttpMethod.Delete, $"/api/organizations/invitations/{mert.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.Get<List<InvitationDto>>("/api/organizations/invitations")).Single(i => i.Id == mert.Id).Status.ShouldBe("Revoked");
        (await maintainerApi.Send(HttpMethod.Get, "/api/organizations/invitations")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Personal_azure_credentials_are_verified_and_used_for_writes()
    {
        var (admin, organizationId) = await AdminAsync();
        var project = await CreateProjectAsync(admin, "OBO");
        var azure = Factory.Azure.Project("OBO");
        Factory.CreateRemoteRepository(azure, "portal");
        azure.AddWorkItem(701, "Fix login redirect", "New", tags: "repo:portal");
        await admin.Post<SyncResult>($"/api/projects/{project.Id}/azure/sync", new { });

        // PAT verification asks Azure who the token belongs to.
        var identities = Factory.Azure.Project("").TokenIdentities;
        identities["janePersonalAccessToken0001"] = new AzureIdentity("Jane Doe", "Jane@Contoso.com");
        identities["joePersonalAccessToken00001"] = new AzureIdentity("Joe Smith", "joe@contoso.com");
        await AddUserAsync(admin, "jane@contoso.com", "Jane Doe");
        var jane = await LoginAsync("jane@contoso.com", organizationId);
        const string url = "https://dev.azure.com/contoso";

        var foreign = await jane.Send(HttpMethod.Put, "/api/me/azure-credentials/pat", new { azureOrganizationUrl = url, personalAccessToken = "joePersonalAccessToken00001" });
        foreign.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await foreign.Content.ReadAsStringAsync()).ShouldContain("belongs to joe@contoso.com");
        (await jane.Send(HttpMethod.Put, "/api/me/azure-credentials/pat", new { azureOrganizationUrl = url, personalAccessToken = "rejectedToken0000000000000" }))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var saved = await jane.Put<AzureCredentialDto>("/api/me/azure-credentials/pat", new { azureOrganizationUrl = url, personalAccessToken = "janePersonalAccessToken0001" });
        saved.AzureUniqueName.ShouldBe("jane@contoso.com");
        var listed = await jane.Http.GetStringAsync("/api/me/azure-credentials");
        listed.ShouldNotContain("janePersonalAccessToken0001");

        // Jane's assignment and her new work item are written with her own PAT.
        await jane.Post<WorkItemSummaryDto>($"/api/projects/{project.Id}/work-items/701/assign", new { toMe = true });
        azure.Writes.Last(w => w.WorkItemId == 701).ShouldBe(("assign", 701, "jane@contoso.com", "Basic"));
        var types = await jane.Get<List<AzureWorkItemType>>($"/api/projects/{project.Id}/work-item-types");
        types.Select(t => t.Name).ShouldBe(["User Story", "Issue"]);
        var created = await jane.Post<WorkItemSummaryDto>($"/api/projects/{project.Id}/work-items", new
        {
            type = "Issue", title = "Add PaginatedList tests", description = "Cover paging edges.\nUse xUnit.", acceptanceCriteria = "Tests pass in CI",
            repositoryNames = new[] { "PORTAL" }, tags = new[] { "agent" }, assignToMe = true, priority = 2,
        });
        created.RepositoryNames.ShouldBe(["portal"]);
        created.AssignedToUniqueName.ShouldBe("jane@contoso.com");
        created.State.ShouldBe("To Do");
        var detail = await jane.Get<WorkItemDetailDto>($"/api/projects/{project.Id}/work-items/{created.AzureId}");
        detail.Tags.ShouldBe(["repo:portal", "agent"]);
        // Basic-process Issues have no acceptance criteria field: the criteria move into the description.
        detail.Description.ShouldContain("<h3>Acceptance criteria</h3><div>Tests pass in CI</div>");
        detail.AcceptanceCriteria.ShouldBeEmpty();
        azure.Writes.Last(w => w.WorkItemId == created.AzureId).ShouldBe(("create", created.AzureId, "jane@contoso.com", "Basic"));
        (await jane.Send(HttpMethod.Post, $"/api/projects/{project.Id}/work-items", new { type = "Issue", title = "x", repositoryNames = new[] { "nope" }, assignToMe = false }))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // Without a personal credential the service identity is used, unless the project requires personal credentials.
        await AddUserAsync(admin, "joe@contoso.com", "Joe Smith");
        var joe = await LoginAsync("joe@contoso.com", organizationId);
        await joe.Post<WorkItemSummaryDto>($"/api/projects/{project.Id}/work-items/701/assign", new { toMe = true });
        azure.Writes.Last(w => w.WorkItemId == 701).ShouldBe(("assign", 701, "service", "Basic"));
        project = await admin.Get<ProjectDto>($"/api/projects/{project.Id}");
        await admin.Put<ProjectDto>($"/api/projects/{project.Id}", Settings(project, new { requireUserCredentialForWrites = true }));
        var blocked = await joe.Send(HttpMethod.Post, $"/api/projects/{project.Id}/work-items/701/assign", new { toMe = true });
        blocked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await blocked.Content.ReadAsStringAsync()).ShouldContain("Connect your Azure DevOps account");
        await jane.Post<WorkItemSummaryDto>($"/api/projects/{project.Id}/work-items/701/assign", new { toMe = true });

        (await jane.Send(HttpMethod.Delete, $"/api/me/azure-credentials/{saved.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await jane.Send(HttpMethod.Post, $"/api/projects/{project.Id}/work-items/701/assign", new { toMe = true })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Entra_sign_in_creates_accounts_only_for_known_people()
    {
        var (admin, organizationId) = await AdminAsync();
        var project = await CreateProjectAsync(admin, "SSO");
        var azure = Factory.Azure.Project("SSO");
        azure.Teams.Clear();
        azure.Teams.Add(new AzureTeam("t-sso", "Platform"));
        azure.TeamMembers["t-sso"] = [new("Sam Sezer", "sam@contoso.com")];
        Factory.CreateRemoteRepository(azure, "infra");
        azure.AddWorkItem(801, "Rotate certificates", "New", tags: "repo:infra");
        const string groupId = "6f1d2c3b-4a5e-4f60-8a7b-9c0d1e2f3a4b";
        project = await admin.Put<ProjectDto>($"/api/projects/{project.Id}", Settings(project, new { groupRoles = new[] { new { groupId, role = "Maintainer" } } }));
        await admin.Post<SyncResult>($"/api/projects/{project.Id}/azure/sync", new { });

        var providers = await NewApi().Get<AuthProvidersDto>("/api/auth/providers");
        providers.Entra.ShouldBeTrue();

        async Task<HttpResponseMessage> SignIn(string code, ExternalIdentity identity)
        {
            Factory.Sso.Codes[code] = identity;
            var start = await NewApi().Post<ExternalSignInStartDto>("/api/auth/entra/start", new { });
            var state = start.AuthorizeUrl.Split("state=")[1];
            return await NewApi().Send(HttpMethod.Post, "/api/auth/entra/callback", new { code, state });
        }

        var stranger = await SignIn("code-stranger", new ExternalIdentity("entra", "tenant:stranger", "stranger@contoso.com", "Stranger", [], null));
        stranger.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await stranger.Content.ReadAsStringAsync()).ShouldContain("do not have access yet");

        // Sam is in the Azure team: account, membership and delegated Azure DevOps token in one sign-in.
        var expired = new OAuthToken("sam-access-old", "rt-sam", DateTimeOffset.UtcNow.AddMinutes(-1));
        var samResponse = await SignIn("code-sam", new ExternalIdentity("entra", "tenant:sam-oid", "Sam@Contoso.com", "Sam Sezer", [], expired));
        samResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var samAuth = JsonSerializer.Deserialize<AuthResult>(await samResponse.Content.ReadAsStringAsync(), AppFactory.Json)!;
        samAuth.User.HasPassword.ShouldBeFalse();
        var sam = NewApi();
        sam.Authenticate(samAuth.AccessToken, organizationId);
        (await sam.Get<ProjectDto>($"/api/projects/{project.Id}")).MyPermissions.ShouldContain(Permission.WorkItemsAssign);
        var credentials = await sam.Get<List<AzureCredentialDto>>("/api/me/azure-credentials");
        credentials.Single().ShouldSatisfyAllConditions(c => c.Kind.ShouldBe(AzureCredentialKind.OAuth), c => c.AzureOrganizationUrl.ShouldBeNull());

        // The expired delegated token is refreshed and the write runs as Sam (Bearer).
        await sam.Post<WorkItemSummaryDto>($"/api/projects/{project.Id}/work-items/801/assign", new { toMe = true });
        Factory.Tokens.Requests.ShouldContain("refresh:rt-sam");
        azure.Writes.Last(w => w.WorkItemId == 801).ShouldBe(("assign", 801, "sam@contoso.com", "Bearer"));

        // Signing in again maps to the same account; password-less accounts cannot use the password form.
        var again = await SignIn("code-sam-2", new ExternalIdentity("entra", "tenant:sam-oid", "sam@contoso.com", "Sam Sezer", [], null));
        JsonSerializer.Deserialize<AuthResult>(await again.Content.ReadAsStringAsync(), AppFactory.Json)!.User.Id.ShouldBe(samAuth.User.Id);
        (await NewApi().Send(HttpMethod.Post, "/api/auth/login", new { email = "sam@contoso.com", password = Password })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        // A replayed state is rejected.
        var replay = await NewApi().Send(HttpMethod.Post, "/api/auth/entra/callback", new { code = "code-sam", state = "unknown-state" });
        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Group mapping: Gina is not in the team but in the mapped Entra ID group.
        var gina = await SignIn("code-gina", new ExternalIdentity("entra", "tenant:gina-oid", "gina@contoso.com", "Gina", [groupId.ToUpperInvariant()], null));
        gina.StatusCode.ShouldBe(HttpStatusCode.OK);
        var members = await admin.Get<List<ProjectMemberDto>>($"/api/projects/{project.Id}/members");
        members.Single(m => m.Email == "gina@contoso.com").ShouldSatisfyAllConditions(m => m.Role.ShouldBe(ProjectRole.Maintainer), m => m.Source.ShouldBe(MembershipSource.EntraGroup));
        members.Single(m => m.Email == "sam@contoso.com").Source.ShouldBe(MembershipSource.AzureTeam);
        // Leaving the group removes the project access it granted at the next sign-in (she stays an organization member).
        (await SignIn("code-gina-2", new ExternalIdentity("entra", "tenant:gina-oid", "gina@contoso.com", "Gina", [], null))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.Get<List<ProjectMemberDto>>($"/api/projects/{project.Id}/members")).ShouldNotContain(m => m.Email == "gina@contoso.com");
    }

    [Fact]
    public async Task Service_principal_replaces_the_pat()
    {
        var (admin, _) = await AdminAsync();
        var project = await CreateProjectAsync(admin, "SPN", servicePat: false);
        var azure = Factory.Azure.Project("SPN");
        azure.AddWorkItem(901, "Use managed identity", "New");

        var missing = await admin.Send(HttpMethod.Post, $"/api/projects/{project.Id}/azure/sync", new { });
        missing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await missing.Content.ReadAsStringAsync()).ShouldContain("AZURE_DEVOPS_SP_CLIENT_ID");

        foreach (var (name, value) in new[] { ("AZURE_DEVOPS_SP_TENANT_ID", "tenant-1"), ("AZURE_DEVOPS_SP_CLIENT_ID", "client-1"), ("AZURE_DEVOPS_SP_CLIENT_SECRET", "secret-1") })
            (await admin.Send(HttpMethod.Put, $"/api/projects/{project.Id}/secrets/{name}", new { value })).EnsureSuccessStatusCode();
        azure.RequiredPat = "sp-token-client-1";
        (await admin.Post<SyncResult>($"/api/projects/{project.Id}/azure/sync", new { })).WorkItems.ShouldBe(1);
        Factory.Tokens.Requests.ShouldContain("sp:tenant-1:client-1");
        await admin.Post<WorkItemSummaryDto>($"/api/projects/{project.Id}/work-items/901/assign", new { toMe = false, uniqueName = "joe@contoso.com" });
        azure.Writes.Last(w => w.WorkItemId == 901).ShouldBe(("assign", 901, "service-principal", "Bearer"));
    }
}
