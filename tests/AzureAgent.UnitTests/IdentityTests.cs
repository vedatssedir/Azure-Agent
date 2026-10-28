using System.Security.Cryptography;
using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Application.Features.Identity;
using AzureAgent.Domain.Azure;
using AzureAgent.Domain.Projects;
using AzureAgent.Infrastructure.AgentRuntime;
using AzureAgent.Infrastructure.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;

namespace AzureAgent.UnitTests;

public sealed class EntraIdTokenTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "k1" };

    private static readonly EntraOptions Options = new()
    {
        Enabled = true, TenantId = "organizations", AllowedTenantIds = [Tenant], ClientId = "client-app", ClientSecret = "s", RedirectUri = "https://agent.test/auth/callback",
    };

    private static string Token(string tenant = Tenant, string audience = "client-app", string nonce = "n-1", SecurityKey? key = null, string? issuer = null,
        DateTime? expires = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["tid"] = tenant, ["oid"] = "object-1", ["preferred_username"] = "Ayse@Contoso.com", ["name"] = "Ayşe Yılmaz", ["nonce"] = nonce,
            ["groups"] = new[] { "g-1", "g-2" },
        };
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? $"https://login.microsoftonline.com/{tenant}/v2.0",
            Audience = audience,
            Claims = claims,
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            NotBefore = (expires ?? DateTime.UtcNow.AddMinutes(10)).AddMinutes(-20),
            IssuedAt = (expires ?? DateTime.UtcNow.AddMinutes(10)).AddMinutes(-20),
            SigningCredentials = new SigningCredentials(key ?? Key, SecurityAlgorithms.RsaSha256),
        });
    }

    [Fact]
    public async Task Valid_token_maps_to_an_external_identity()
    {
        var identity = await EntraIdentityProvider.ValidateIdTokenAsync(Token(), Options, [Key], "n-1");
        identity.Subject.ShouldBe($"{Tenant}:object-1");
        identity.Email.ShouldBe("Ayse@Contoso.com");
        identity.DisplayName.ShouldBe("Ayşe Yılmaz");
        identity.Groups.ShouldBe(["g-1", "g-2"]);
    }

    [Fact]
    public async Task Tokens_from_other_tenants_audiences_keys_or_nonces_are_rejected()
    {
        async Task Rejected(string token, string nonce = "n-1") =>
            await Should.ThrowAsync<UnauthorizedException>(() => EntraIdentityProvider.ValidateIdTokenAsync(token, Options, [Key], nonce));

        await Rejected(Token(tenant: "22222222-2222-2222-2222-222222222222"));
        await Rejected(Token(audience: "other-app"));
        await Rejected(Token(key: new RsaSecurityKey(RSA.Create(2048)) { KeyId = "k1" }));
        await Rejected(Token(issuer: "https://evil.example/v2.0"));
        await Rejected(Token(expires: DateTime.UtcNow.AddMinutes(-10)));
        await Rejected(Token(), nonce: "other-nonce");
    }

    [Fact]
    public void Multi_tenant_sign_in_requires_an_allow_list()
    {
        new EntraOptions { Enabled = true, ClientId = "c", ClientSecret = "s", RedirectUri = "https://x/cb" }.Problems()
            .ShouldContain("Entra:AllowedTenantIds is required for multi-tenant sign-in");
        new EntraOptions { Enabled = true, TenantId = Tenant, ClientId = "c", ClientSecret = "s", RedirectUri = "https://x/cb" }.Problems().ShouldBeEmpty();
        new EntraOptions { Enabled = false }.Problems().ShouldBeEmpty();
        new EntraOptions { TenantId = Tenant }.TenantAllowed(Tenant.ToUpperInvariant()).ShouldBeTrue();
    }
}

public sealed class CredentialTests
{
    [Fact]
    public void Credentials_build_basic_or_bearer_headers_and_never_print_the_secret()
    {
        AzureCredential.Pat("abc").HeaderValue.ShouldBe("Basic OmFiYw==");
        AzureCredential.Bearer("eyJ0").HeaderValue.ShouldBe("Bearer eyJ0");
        AzureCredential.Pat("abc", "jane@contoso.com").ToString().ShouldBe("Basic credential of jane@contoso.com");
    }

    [Fact]
    public void Git_uses_the_bearer_token_of_a_service_principal()
    {
        var env = GitWorkspace.BuildEnvironment("https://dev.azure.com/contoso", AzureCredential.Bearer("token-1"));
        env["GIT_CONFIG_VALUE_0"].ShouldBe("Authorization: Bearer token-1");
    }
}

public sealed class ProvisioningRuleTests
{
    private static Team Team(string name, params string[] members) => new()
    {
        AzureId = name, Name = name, Members = members.Select(m => new TeamMember { UniqueName = m, DisplayName = m }).ToList(),
    };

    [Fact]
    public void Without_mappings_every_team_member_is_a_developer()
    {
        var roles = MembershipProvisioner.DesiredRoles(new OrganizationSettings(), [Team("Backend", "A@x.com"), Team("QA", "b@x.com")]);
        roles.ShouldBe(new Dictionary<string, ProjectRole> { ["a@x.com"] = ProjectRole.Developer, ["b@x.com"] = ProjectRole.Developer });
    }

    [Fact]
    public void Mapped_teams_only_and_the_highest_role_wins()
    {
        var settings = new OrganizationSettings
        {
            TeamRoles = [new TeamRoleMapping { Team = "backend", Role = ProjectRole.Developer }, new TeamRoleMapping { Team = "Leads", Role = ProjectRole.Maintainer }],
        };
        var roles = MembershipProvisioner.DesiredRoles(settings, [Team("Backend", "a@x.com", "lead@x.com"), Team("Leads", "lead@x.com"), Team("Marketing", "m@x.com")]);
        roles.ShouldBe(new Dictionary<string, ProjectRole> { ["a@x.com"] = ProjectRole.Developer, ["lead@x.com"] = ProjectRole.Maintainer });
    }

    [Fact]
    public void Group_role_matches_object_ids_case_insensitively()
    {
        var settings = new OrganizationSettings { GroupRoles = [new GroupRoleMapping { GroupId = "abc", Role = ProjectRole.Reviewer }] };
        MembershipProvisioner.GroupRole(settings, ["ABC"]).ShouldBe(ProjectRole.Reviewer);
        MembershipProvisioner.GroupRole(settings, ["other"]).ShouldBeNull();
    }

    [Fact]
    public void Team_board_matches_area_paths_and_children()
    {
        var filter = WorkItemProjection.TeamAreaFilter([new TeamArea { Path = @"Shop\Web", IncludeChildren = true }, new TeamArea { Path = @"Shop\Ops" }]).Compile();
        WorkItem Item(string? area) => new() { Type = "", Title = "", State = "", WebUrl = "", AreaPath = area };
        filter(Item(@"Shop\Web")).ShouldBeTrue();
        filter(Item(@"Shop\Web\Checkout")).ShouldBeTrue();
        filter(Item(@"Shop\Website")).ShouldBeFalse();
        filter(Item(@"Shop\Ops")).ShouldBeTrue();
        filter(Item(@"Shop\Ops\Infra")).ShouldBeFalse();
        filter(Item(null)).ShouldBeFalse();
    }

    [Fact]
    public void Invitation_tokens_are_random_and_only_their_hash_is_stored()
    {
        var a = InvitationService.NewToken();
        a.ShouldNotBe(InvitationService.NewToken());
        InvitationService.Hash(a).ShouldNotContain(a);
        InvitationService.Hash(a).ShouldBe(InvitationService.Hash(a));
        InvitationService.LooksLikeEmail("ali@contoso.com").ShouldBeTrue();
        InvitationService.LooksLikeEmail("CONTOSO\\ali").ShouldBeFalse();
    }
}
