using AzureAgent.Application.Features.Audit;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Application.Features.Identity;
using AzureAgent.Application.Features.Organizations;
using AzureAgent.Application.Features.Projects;
using AzureAgent.Application.Features.Settings;
using AzureAgent.Domain.Azure;
using AzureAgent.Domain.Identity;
using AzureAgent.Domain.Projects;
using Carter;
using Wolverine;

namespace AzureAgent.Api.Modules;

/// <summary>The one place for configuration: Azure DevOps, AI providers, automation, users, repositories, audit.</summary>
public sealed class SettingsModule : ICarterModule
{
    public sealed record CredentialRequest(string Value);
    public sealed record RoleRequest(OrganizationRole Role);
    public sealed record ProjectRoleRequest(ProjectRole Role, string? AzureUniqueName);
    public sealed record ActiveRequest(bool IsActive);
    public sealed record AccessRuleRequest(Guid? UserId, ProjectRole? Role, string Permission, AccessEffect Effect);
    public sealed record BudgetRequest(decimal? MonthlyBudgetUsd);

    public void AddRoutes(IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/settings").WithTags("Settings").RequireAuthorization();
        settings.MapGet("/", (IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<SettingsDto>(new GetSettingsQuery(), ct));
        settings.MapGet("/providers", (IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<ProviderStatusListDto>(new ListProviderStatusQuery(), ct));

        settings.MapPut("/azure", (SaveAzureConnectionCommand c, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<AzureConnectionDto>(c, ct));
        settings.MapPost("/azure/sync", async (IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync<string>(new SyncNowCommand(), ct);
            return Results.Accepted();
        });
        settings.MapPost("/azure/service-hook-password", async (IMessageBus bus, CancellationToken ct) =>
            new { password = await bus.InvokeAsync<string>(new RotateServiceHookPasswordCommand(), ct) });

        settings.MapPut("/ai/credentials/{name}", async (string name, CredentialRequest r, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new SetProviderCredentialCommand(name, r.Value), ct);
            return Results.NoContent();
        });
        settings.MapDelete("/ai/credentials/{name}", async (string name, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new DeleteProviderCredentialCommand(name), ct);
            return Results.NoContent();
        });
        settings.MapPost("/ai/{provider}/test", (string provider, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<ProviderTestResult>(new TestProviderCommand(provider), ct));

        settings.MapPut("/automation", (SaveAutomationCommand c, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<OrganizationSettings>(c, ct));
        settings.MapPut("/projects/{projectId:guid}/budget", async (Guid projectId, BudgetRequest r, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new SetProjectBudgetCommand(projectId, r.MonthlyBudgetUsd), ct);
            return Results.NoContent();
        });

        settings.MapGet("/users", (IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<UsersDto>(new ListUsersQuery(), ct));
        settings.MapPut("/users/{userId:guid}/role", async (Guid userId, RoleRequest r, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new SetOrganizationRoleCommand(userId, r.Role), ct);
            return Results.NoContent();
        });
        settings.MapPut("/users/{userId:guid}/active", async (Guid userId, ActiveRequest r, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new SetUserActiveCommand(userId, r.IsActive), ct);
            return Results.NoContent();
        });
        settings.MapPut("/users/{userId:guid}/projects/{projectId:guid}", async (Guid userId, Guid projectId, ProjectRoleRequest r, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new SetProjectMemberCommand(projectId, userId, r.Role, r.AzureUniqueName), ct);
            return Results.NoContent();
        });
        settings.MapDelete("/users/{userId:guid}/projects/{projectId:guid}", async (Guid userId, Guid projectId, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new RemoveProjectMemberCommand(projectId, userId), ct);
            return Results.NoContent();
        });
        settings.MapPost("/invitations", (CreateInvitationsCommand c, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<IReadOnlyList<InvitationLinkDto>>(c, ct));
        settings.MapPost("/invitations/{id:guid}/renew", (Guid id, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<InvitationLinkDto>(new RenewInvitationCommand(id), ct));
        settings.MapDelete("/invitations/{id:guid}", async (Guid id, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new RevokeInvitationCommand(id), ct);
            return Results.NoContent();
        });

        settings.MapPut("/repositories/{repositoryId:guid}/policy", async (Guid repositoryId, RepositoryPolicy policy, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new UpdateRepositoryPolicyCommand(repositoryId, policy), ct);
            return Results.NoContent();
        });
        settings.MapGet("/repositories/{repositoryId:guid}/access-rules", (Guid repositoryId, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<IReadOnlyList<RepositoryAccessRuleDto>>(new ListRepositoryAccessRulesQuery(repositoryId), ct));
        settings.MapPost("/repositories/{repositoryId:guid}/access-rules", (Guid repositoryId, AccessRuleRequest r, IMessageBus bus, CancellationToken ct) =>
            bus.InvokeAsync<RepositoryAccessRuleDto>(new AddRepositoryAccessRuleCommand(repositoryId, r.UserId, r.Role, r.Permission, r.Effect), ct));
        settings.MapDelete("/repositories/{repositoryId:guid}/access-rules/{ruleId:guid}", async (Guid repositoryId, Guid ruleId, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new RemoveRepositoryAccessRuleCommand(repositoryId, ruleId), ct);
            return Results.NoContent();
        });

        settings.MapGet("/audit", (Guid? project, IMessageBus bus, CancellationToken ct) => bus.InvokeAsync<IReadOnlyList<AuditEntryDto>>(new ListAuditQuery(project), ct));
        app.MapGet("/api/permissions", () => Permission.All).AllowAnonymous().WithTags("Settings");
    }
}
