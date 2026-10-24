using AzureAgent.Api.Infrastructure;
using AzureAgent.Application.Features.Auth;
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

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth").RequireRateLimiting("auth");
        auth.MapPost("/setup", (SetupRequest r, IMessageBus bus, HttpContext http, CancellationToken ct) =>
            bus.InvokeAsync<AuthResult>(new SetupCommand(r.Email, r.DisplayName, r.Password, r.OrganizationName, http.ClientIp()), ct)).AllowAnonymous();
        auth.MapPost("/login", (LoginRequest r, IMessageBus bus, HttpContext http, CancellationToken ct) =>
            bus.InvokeAsync<AuthResult>(new LoginCommand(r.Email, r.Password, http.ClientIp()), ct)).AllowAnonymous();
        auth.MapPost("/refresh", (RefreshRequest r, IMessageBus bus, HttpContext http, CancellationToken ct) =>
            bus.InvokeAsync<AuthResult>(new RefreshCommand(r.RefreshToken, http.ClientIp()), ct)).AllowAnonymous();
        auth.MapPost("/logout", async (RefreshRequest r, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new LogoutCommand(r.RefreshToken), ct);
            return Results.NoContent();
        }).AllowAnonymous();
        auth.MapGet("/me", (IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<MeDto>(new GetMeQuery(), ct)).RequireAuthorization();
        auth.MapPost("/change-password", async (ChangePasswordCommand c, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(c, ct);
            return Results.NoContent();
        }).RequireAuthorization();

        var orgs = app.MapGroup("/api/organizations").WithTags("Organizations").RequireAuthorization();
        orgs.MapPost("/", (CreateOrganizationCommand c, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<OrganizationMembershipDto>(c, ct));
        orgs.MapGet("/members", (IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<IReadOnlyList<OrganizationMemberDto>>(new ListOrganizationMembersQuery(), ct));
        orgs.MapPost("/members", (AddOrganizationMemberCommand c, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<OrganizationMemberDto>(c, ct));
        orgs.MapPut("/members/{userId:guid}/active", async (Guid userId, ActiveRequest r, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new SetUserActiveCommand(userId, r.IsActive), ct);
            return Results.NoContent();
        });
    }

    public sealed record ActiveRequest(bool IsActive);
}
