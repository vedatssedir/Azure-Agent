using System.Net;
using System.Text.Json;
using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Features.Agents;
using AzureAgent.Application.Features.Auth;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Application.Features.Identity;
using AzureAgent.Application.Features.Projects;
using AzureAgent.Application.Features.Settings;
using AzureAgent.Domain.Identity;
using AzureAgent.Domain.Projects;
using Shouldly;

namespace AzureAgent.IntegrationTests;

/// <summary>Azure team sync → people and roles, invitations, personal Azure credentials, Entra ID sign-in, service principal.</summary>
[Collection(PlatformCollection.Name)]
public sealed class IdentityTests(AppFactory factory) : PlatformTestBase(factory)
{
    [Fact]
    public async Task Azure_team_members_get_mapped_roles_invitations_and_team_boards()
    {
        var (admin, organizationId) = await AdminAsync();
        await UpdateSettingsAsync(admin, new
        {
            teamRoles = new[] { new { team = "Backend", role = "Developer" }, new { team = "QA", role = "Reviewer" } },
            autoInviteTeamMembers = true,
        });
        try
        {
            var project = await NewProjectAsync(admin, organizationId, "Team", azure =>
            {
                azure.Teams.Clear();
                azure.Teams.AddRange([new AzureTeam("t-backend", "Backend"), new AzureTeam("t-qa", "QA")]);
                azure.TeamMembers["t-backend"] = [new("Ali Kaya", "Ali@Contoso.com"), new("Bea Demir", "bea@contoso.com")];
                azure.TeamMembers["t-qa"] = [new("Quinn QA", "qa@contoso.com"), new("Bea Demir", "bea@contoso.com")];
                azure.TeamAreas["t-backend"] = [("Team\\Backend", true)];
                azure.TeamAreas["t-qa"] = [("Team\\QA", false)];
                azure.AddWorkItem(601, "Backend child area item", "To Do", areaPath: "Team\\Backend\\Api");
                azure.AddWorkItem(602, "QA item", "To Do", areaPath: "Team\\QA", assignee: "qa@contoso.com");
                azure.AddWorkItem(603, "Root item", "To Do", areaPath: "Team");
            });

            var ali = await SignUpAsync("ali@contoso.com", "Ali Kaya", organizationId);
            var qa = await SignUpAsync("qa@contoso.com", "Quinn QA", organizationId);
            (await ali.Get<ProjectDto>($"/api/projects/{project.Id}")).MyRole.ShouldBe(ProjectRole.Developer);
            (await qa.Get<ProjectDto>($"/api/projects/{project.Id}")).MyRole.ShouldBe(ProjectRole.Reviewer);
            (await ali.Get<List<AgentDto>>("/api/agents?mine=true")).ShouldHaveSingleItem().IsDefault.ShouldBeTrue();
            (await qa.Get<List<AgentDto>>("/api/agents?mine=true")).ShouldHaveSingleItem(); // reviewers can do everything developers can

            // Bea has no account yet: she was invited and shows up as a person of the organization.
            var users = await admin.Get<UsersDto>("/api/settings/users");
            users.WithoutAccount.Single(p => p.UniqueName == "bea@contoso.com").ShouldSatisfyAllConditions(
                p => p.Projects.ShouldBe(["Team"]), p => p.InvitationStatus.ShouldBe("Pending"));

            // Boards and lists follow each team's area paths (children only where the team says so).
            var teams = await admin.Get<List<TeamDto>>($"/api/projects/{project.Id}/teams");
            var backend = teams.Single(t => t.Name == "Backend");
            (await admin.Get<List<WorkItemSummaryDto>>($"/api/projects/{project.Id}/work-items?team={backend.Id}")).Select(w => w.AzureId).ShouldBe([601]);
            var qaBoard = await admin.Get<BoardDto>($"/api/projects/{project.Id}/board?team={teams.Single(t => t.Name == "QA").Id}");
            qaBoard.Columns.SelectMany(c => c.Items).Select(i => i.AzureId).ShouldBe([602]);
            (await qa.Get<BoardDto>($"/api/projects/{project.Id}/board?team={teams.Single(t => t.Name == "QA").Id}&mine=true")).Columns.SelectMany(c => c.Items).ShouldHaveSingleItem();
            (await admin.Get<List<WorkItemSummaryDto>>($"/api/projects/{project.Id}/work-items?unassigned=true")).Select(w => w.AzureId).ShouldBe([601, 603], ignoreOrder: true);

            // Leaving the team removes the synced membership; a manually set one stays.
            var qaId = (await qa.Get<MeDto>("/api/auth/me")).User.Id;
            (await admin.Send(HttpMethod.Put, $"/api/settings/users/{qaId}/projects/{project.Id}", new { role = ProjectRole.Maintainer })).EnsureSuccessStatusCode();
            var azure = Azure.Project("Team");
            azure.TeamMembers["t-backend"] = [new("Bea Demir", "bea@contoso.com")];
            azure.TeamMembers["t-qa"] = [];
            (await SyncAsync(organizationId, project.Id)).MembersRemoved.ShouldBe(1);
            (await ali.Send(HttpMethod.Get, $"/api/projects/{project.Id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await qa.Get<ProjectDto>($"/api/projects/{project.Id}")).MyRole.ShouldBe(ProjectRole.Maintainer);
        }
        finally { await UpdateSettingsAsync(admin, new { teamRoles = Array.Empty<object>(), autoInviteTeamMembers = false }); }
    }

    [Fact]
    public async Task Invited_people_accept_and_admins_manage_invitations()
    {
        var (admin, organizationId) = await AdminAsync();
        var project = await NewProjectAsync(admin, organizationId, "Invite", azure =>
        {
            azure.Teams.Clear();
            azure.Teams.Add(new AzureTeam("t-web", "Web"));
            azure.TeamMembers["t-web"] = [new("Nur Aksoy", "nur@contoso.com")];
        });
        var link = (await admin.Post<List<InvitationLinkDto>>("/api/settings/invitations",
            new { people = new[] { new { email = "nur@contoso.com", displayName = "Nur Aksoy" } }, role = "Member" })).Single();
        link.Url.ShouldStartWith("https://agent.test/invite/");
        var token = Uri.UnescapeDataString(link.Url.Split("/invite/")[1]);
        (await NewApi().Get<InvitationInfoDto>($"/api/auth/invitations/{token}")).ShouldSatisfyAllConditions(i => i.Email.ShouldBe("nur@contoso.com"), i => i.HasAccount.ShouldBeFalse());
        (await NewApi().Send(HttpMethod.Post, "/api/auth/invitations/accept", new { token, password = "weak" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var auth = await NewApi().Post<AuthResult>("/api/auth/invitations/accept", new { token, displayName = "Nur A.", password = Password });
        auth.User.DisplayName.ShouldBe("Nur A.");
        (await NewApi().Send(HttpMethod.Post, "/api/auth/invitations/accept", new { token, password = Password })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var nur = NewApi();
        nur.Authenticate(auth.AccessToken, organizationId);
        (await nur.Get<ProjectDto>($"/api/projects/{project.Id}")).MyPermissions.ShouldContain(Permission.WorkItemsAssign);

        var mert = (await admin.Post<List<InvitationLinkDto>>("/api/settings/invitations",
            new { people = new[] { new { email = "mert@contoso.com", displayName = "Mert Ak" } }, role = "Member" })).Single();
        var renewed = await admin.Post<InvitationLinkDto>($"/api/settings/invitations/{mert.Id}/renew", new { });
        renewed.Url.ShouldNotBe(mert.Url);
        (await admin.Send(HttpMethod.Delete, $"/api/settings/invitations/{mert.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.Get<UsersDto>("/api/settings/users")).Invitations.Single(i => i.Id == mert.Id).Status.ShouldBe("Revoked");
        (await nur.Send(HttpMethod.Post, "/api/settings/invitations", new { people = new[] { new { email = "x@contoso.com", displayName = "X" } }, role = "Member" }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Writes_use_the_personal_credential_unless_the_service_identity_is_allowed()
    {
        var (admin, organizationId) = await AdminAsync();
        var project = await NewProjectAsync(admin, organizationId, "Obo", azure => azure.AddWorkItem(701, "Fix login redirect", "To Do"));
        var azure = Azure.Project("Obo");
        var joe = await DeveloperAsync("joe@contoso.com", "Joe Smith", organizationId);
        await joe.Post<WorkItemSummaryDto>($"/api/projects/{project.Id}/work-items/701/assign", new { toMe = true });
        azure.Writes.Last(w => w.WorkItemId == 701).Owner.ShouldBe("joe@contoso.com");

        // Without his PAT the service identity writes, unless the organization requires personal credentials.
        var credential = (await joe.Get<List<AzureCredentialDto>>("/api/me/azure-credentials")).Single();
        (await joe.Send(HttpMethod.Delete, $"/api/me/azure-credentials/{credential.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await joe.Post<WorkItemSummaryDto>($"/api/projects/{project.Id}/work-items/701/assign", new { toMe = false, uniqueName = "someone@contoso.com" });
        azure.Writes.Last(w => w.WorkItemId == 701).Owner.ShouldBe("service");
        await UpdateSettingsAsync(admin, new { requireUserCredentialForWrites = true });
        try
        {
            var blocked = await joe.Send(HttpMethod.Post, $"/api/projects/{project.Id}/work-items/701/assign", new { toMe = true });
            blocked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await blocked.Content.ReadAsStringAsync()).ShouldContain("Connect your Azure DevOps account");
            // Reconnecting is verified against Azure: the token must be his.
            var pat = Azure.TokenIdentities.First(t => t.Value.UniqueName == "joe@contoso.com").Key;
            const string jane = "janeTokenOfferedByJoe000000000";
            Azure.TokenIdentities[jane] = new AzureIdentity("Jane Doe", "jane@contoso.com");
            (await joe.Send(HttpMethod.Put, "/api/me/azure-credentials/pat", new { azureOrganizationUrl = OrganizationUrl, personalAccessToken = jane })).StatusCode
                .ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await joe.Put<AzureCredentialDto>("/api/me/azure-credentials/pat", new { azureOrganizationUrl = OrganizationUrl, personalAccessToken = pat })).AzureUniqueName.ShouldBe("joe@contoso.com");
            await joe.Post<WorkItemSummaryDto>($"/api/projects/{project.Id}/work-items/701/assign", new { toMe = true });
            (await joe.Http.GetStringAsync("/api/me/azure-credentials")).ShouldNotContain(pat);
        }
        finally { await UpdateSettingsAsync(admin, new { requireUserCredentialForWrites = false }); }
    }

    [Fact]
    public async Task A_personal_credential_azure_rejects_falls_back_to_the_service_identity()
    {
        var (admin, organizationId) = await AdminAsync();
        var project = await NewProjectAsync(admin, organizationId, "Rejected", azure => azure.AddWorkItem(711, "Tidy logging", "To Do"));
        var azure = Azure.Project("Rejected");
        var joe = await DeveloperAsync("joe@contoso.com", "Joe Smith", organizationId);
        var pat = Azure.TokenIdentities.First(t => t.Value.UniqueName == "joe@contoso.com").Key;
        Azure.ReadOnlyTokens.Add(pat);
        try
        {
            var board = await joe.Get<BoardDto>($"/api/projects/{project.Id}/board");
            (await joe.Post<WorkItemSummaryDto>($"/api/projects/{project.Id}/work-items/711/move", new { board.TeamId, board = "Issues", column = "Doing" }))
                .State.ShouldBe("Doing");
            azure.Writes.Last(w => w.WorkItemId == 711).Owner.ShouldBe("service");

            await UpdateSettingsAsync(admin, new { requireUserCredentialForWrites = true });
            var blocked = await joe.Send(HttpMethod.Post, $"/api/projects/{project.Id}/work-items/711/move", new { board.TeamId, board = "Issues", column = "Done" });
            blocked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await blocked.Content.ReadAsStringAsync()).ShouldContain("rejected your Azure connection");
        }
        finally
        {
            Azure.ReadOnlyTokens.Remove(pat);
            await UpdateSettingsAsync(admin, new { requireUserCredentialForWrites = false });
        }
    }

    [Fact]
    public async Task Entra_sign_in_creates_accounts_only_for_known_people()
    {
        var (admin, organizationId) = await AdminAsync();
        const string groupId = "6f1d2c3b-4a5e-4f60-8a7b-9c0d1e2f3a4b";
        await UpdateSettingsAsync(admin, new { groupRoles = new[] { new { groupId, role = "Maintainer" } } });
        try
        {
            var project = await NewProjectAsync(admin, organizationId, "Sso", azure =>
            {
                azure.Teams.Clear();
                azure.Teams.Add(new AzureTeam("t-sso", "Platform"));
                azure.TeamMembers["t-sso"] = [new("Sam Sezer", "sam@contoso.com")];
                azure.AddWorkItem(801, "Rotate certificates", "To Do");
            });
            (await NewApi().Get<AuthProvidersDto>("/api/auth/providers")).Entra.ShouldBeTrue();

            async Task<HttpResponseMessage> SignIn(string code, ExternalIdentity identity)
            {
                Factory.Sso.Codes[code] = identity;
                var start = await NewApi().Post<ExternalSignInStartDto>("/api/auth/entra/start", new { });
                return await NewApi().Send(HttpMethod.Post, "/api/auth/entra/callback", new { code, state = start.AuthorizeUrl.Split("state=")[1] });
            }

            var stranger = await SignIn("code-stranger", new ExternalIdentity("entra", "tenant:stranger", "stranger@contoso.com", "Stranger", [], null));
            stranger.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

            var expired = new OAuthToken("sam-access-old", "rt-sam", DateTimeOffset.UtcNow.AddMinutes(-1));
            var samResponse = await SignIn("code-sam", new ExternalIdentity("entra", "tenant:sam-oid", "Sam@Contoso.com", "Sam Sezer", [], expired));
            samResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
            var samAuth = JsonSerializer.Deserialize<AuthResult>(await samResponse.Content.ReadAsStringAsync(), AppFactory.Json)!;
            samAuth.User.HasPassword.ShouldBeFalse();
            var sam = NewApi();
            sam.Authenticate(samAuth.AccessToken, organizationId);
            (await sam.Get<ProjectDto>($"/api/projects/{project.Id}")).MyPermissions.ShouldContain(Permission.WorkItemsAssign);
            (await sam.Get<List<AzureCredentialDto>>("/api/me/azure-credentials")).Single().Kind.ShouldBe(AzureCredentialKind.OAuth);

            // The expired delegated token is refreshed and the write runs as Sam (Bearer).
            await sam.Post<WorkItemSummaryDto>($"/api/projects/{project.Id}/work-items/801/assign", new { toMe = true });
            Factory.Tokens.Requests.ShouldContain("refresh:rt-sam");
            Azure.Project("Sso").Writes.Last(w => w.WorkItemId == 801).ShouldBe(("assign", 801, "sam@contoso.com", "Bearer"));
            (await NewApi().Send(HttpMethod.Post, "/api/auth/login", new { email = "sam@contoso.com", password = Password })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

            // Entra group mapping: Gina is in no team but in the mapped group.
            (await SignIn("code-gina", new ExternalIdentity("entra", "tenant:gina-oid", "gina@contoso.com", "Gina", [groupId.ToUpperInvariant()], null))).StatusCode.ShouldBe(HttpStatusCode.OK);
            var gina = (await admin.Get<UsersDto>("/api/settings/users")).Users.Single(u => u.Email == "gina@contoso.com");
            gina.Projects.Single(p => p.ProjectId == project.Id).ShouldSatisfyAllConditions(p => p.Role.ShouldBe(ProjectRole.Maintainer), p => p.Source.ShouldBe(MembershipSource.EntraGroup));
            gina.SignsInWithMicrosoft.ShouldBeTrue();
        }
        finally { await UpdateSettingsAsync(admin, new { groupRoles = Array.Empty<object>() }); }
    }

    [Fact]
    public async Task Service_principal_replaces_the_pat_and_a_pat_replaces_it_again()
    {
        var (admin, organizationId) = await AdminAsync();
        var project = await NewProjectAsync(admin, organizationId, "Spn", azure => azure.AddWorkItem(901, "Use managed identity", "To Do"));
        try
        {
            var saved = await admin.Put<AzureConnectionDto>("/api/settings/azure", new
            {
                organizationUrl = OrganizationUrl, servicePrincipalTenantId = "tenant-1", servicePrincipalClientId = "client-1", servicePrincipalClientSecret = "secret-1",
            });
            saved.ShouldSatisfyAllConditions(a => a.CredentialType.ShouldBe("service-principal"), a => a.ServicePrincipalConfigured.ShouldBeTrue());
            Factory.Tokens.Requests.ShouldContain("sp:tenant-1:client-1");
            // The service identity (sync) now authenticates with the service principal's token.
            (await SyncAsync(organizationId, project.Id)).Teams.ShouldBe(1);
            (await admin.Send(HttpMethod.Put, "/api/settings/azure", new { organizationUrl = "https://dev.azure.com/other" })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }
        finally
        {
            var restored = await admin.Put<AzureConnectionDto>("/api/settings/azure", new { organizationUrl = OrganizationUrl, personalAccessToken = AdminPat });
            restored.ShouldSatisfyAllConditions(a => a.CredentialType.ShouldBe("pat"), a => a.ServicePrincipalConfigured.ShouldBeFalse());
        }
    }
}
