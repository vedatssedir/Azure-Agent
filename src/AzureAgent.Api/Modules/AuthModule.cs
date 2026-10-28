using AzureAgent.Api.Infrastructure;
using AzureAgent.Application.Features.Auth;
using AzureAgent.Application.Features.Identity;
using AzureAgent.Application.Features.Organizations;
using AzureAgent.Domain.Identity;
using Carter;
using Wolverine;

namespace AzureAgent.Api.Modules;

public sealed class AuthModule : ICarterModule
{
    public sealed record SetupRequest(string Email, string DisplayName, string Password, string OrganizationName);
    public sealed record LoginRequest(string Email, string Password);
    public sealed record RefreshRequest(string RefreshToken);
    public sealed record ExternalCallbackRequest(string Code, string State);
    public sealed record AcceptInvitationRequest(string Token, string? DisplayName, string Password);
    public sealed record PatRequest(string AzureOrganizationUrl, string PersonalAccessToken, DateTimeOffset? ExpiresAt);

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");
        auth.MapPost("/setup", (SetupRequest r, IMessageBus bus, HttpContext http, CancellationToken ct) =>
            bus.InvokeAsync<AuthResult>(new SetupCommand(r.Email, r.DisplayName, r.Password, r.OrganizationName, http.ClientIp()), ct)).AllowAnonymous().RequireRateLimiting("auth");
        auth.MapPost("/login", (LoginRequest r, IMessageBus bus, HttpContext http, CancellationToken ct) =>
            bus.InvokeAsync<AuthResult>(new LoginCommand(r.Email, r.Password, http.ClientIp()), ct)).AllowAnonymous().RequireRateLimiting("auth");
        auth.MapPost("/refresh", (RefreshRequest r, IMessageBus bus, HttpContext http, CancellationToken ct) =>
            bus.InvokeAsync<AuthResult>(new RefreshCommand(r.RefreshToken, http.ClientIp()), ct)).AllowAnonymous().RequireRateLimiting("refresh");
        auth.MapPost("/logout", async (RefreshRequest r, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new LogoutCommand(r.RefreshToken), ct);
            return Results.NoContent();
        }).AllowAnonymous();
        auth.MapGet("/me", (IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<MeDto>(new GetMeQuery(), ct)).RequireAuthorization();
        auth.MapGet("/providers", (IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<AuthProvidersDto>(new GetAuthProvidersQuery(), ct)).AllowAnonymous();
        auth.MapPost("/entra/start", (IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<ExternalSignInStartDto>(new BeginExternalSignInCommand(), ct)).AllowAnonymous().RequireRateLimiting("auth");
        auth.MapPost("/entra/callback", (ExternalCallbackRequest r, IMessageBus bus, HttpContext http, CancellationToken ct) =>
            bus.InvokeAsync<AuthResult>(new CompleteExternalSignInCommand(r.Code, r.State, http.ClientIp()), ct)).AllowAnonymous().RequireRateLimiting("auth");
        auth.MapGet("/invitations/{token}", (string token, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<InvitationInfoDto>(new GetInvitationQuery(token), ct)).AllowAnonymous().RequireRateLimiting("auth");
        auth.MapPost("/invitations/accept", (AcceptInvitationRequest r, IMessageBus bus, HttpContext http, CancellationToken ct) =>
            bus.InvokeAsync<AuthResult>(new AcceptInvitationCommand(r.Token, r.DisplayName, r.Password, http.ClientIp()), ct)).AllowAnonymous().RequireRateLimiting("auth");

        var me = app.MapGroup("/api/me").WithTags("Me").RequireAuthorization();
        me.MapGet("/azure-credentials", (IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<AzureCredentialDto>>(new ListMyAzureCredentialsQuery(), ct));
        me.MapPut("/azure-credentials/pat", (PatRequest r, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<AzureCredentialDto>(new SaveMyAzurePatCommand(r.AzureOrganizationUrl, r.PersonalAccessToken, r.ExpiresAt), ct)).RequireRateLimiting("auth");
        me.MapDelete("/azure-credentials/{id:guid}", async (Guid id, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new DeleteMyAzureCredentialCommand(id), ct);
            return Results.NoContent();
        });
        auth.MapPost("/change-password", async (ChangePasswordCommand c, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(c, ct);
            return Results.NoContent();
        }).RequireAuthorization();

        var orgs = app.MapGroup("/api/organizations").WithTags("Organizations").RequireAuthorization();
        orgs.MapPost("/", (CreateOrganizationCommand c, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<OrganizationMembershipDto>(c, ct));
        orgs.MapGet("/members", (IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<IReadOnlyList<OrganizationMemberDto>>(new ListOrganizationMembersQuery(), ct));
        orgs.MapPost("/members", (AddOrganizationMemberCommand c, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<OrganizationMemberDto>(c, ct));
        orgs.MapGet("/invitations", (IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<IReadOnlyList<InvitationDto>>(new ListInvitationsQuery(), ct));
        orgs.MapPost("/invitations", (CreateInvitationsCommand c, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<IReadOnlyList<InvitationLinkDto>>(c, ct));
        orgs.MapPost("/invitations/{id:guid}/renew", (Guid id, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<InvitationLinkDto>(new RenewInvitationCommand(id), ct));
        orgs.MapDelete("/invitations/{id:guid}", async (Guid id, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new RevokeInvitationCommand(id), ct);
            return Results.NoContent();
        });
        orgs.MapPut("/members/{userId:guid}/active", async (Guid userId, ActiveRequest r, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new SetUserActiveCommand(userId, r.IsActive), ct);
            return Results.NoContent();
        });
    }

    public sealed record ActiveRequest(bool IsActive);
}
