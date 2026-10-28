using System.Security.Cryptography;
using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Application.Features.Projects;
using AzureAgent.Application.Features.Secrets;
using AzureAgent.Domain.Agents;
using AzureAgent.Domain.Projects;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Features.Settings;

public sealed record AzureConnectionDto(string? OrganizationUrl, string OrganizationName, string CredentialType, bool PatConfigured, bool ServicePrincipalConfigured,
    DateTimeOffset? LastSyncedAt, string? LastSyncError, int Projects, int Repositories, int MirroredRepositories, int Teams, int WorkItems,
    bool ServiceHookPasswordConfigured);

public sealed record AiCredentialDto(string Name, string Label, bool Configured, DateTimeOffset? UpdatedAt);

/// <summary>One AI backend: an API provider (key) or a coding CLI (installed executable + credential).</summary>
public sealed record AiProviderDto(string Key, string Name, string Kind, AgentRuntime Runtime, ModelProvider Provider, string DefaultModel, string Description,
    IReadOnlyList<AiCredentialDto> Credentials, bool Configured, bool? Installed, string? Version, string? InstallHint);

public sealed record SettingsDto(AzureConnectionDto Azure, OrganizationSettings Automation, decimal? MonthlyBudgetUsd, IReadOnlyList<AiProviderDto> Providers,
    bool EntraEnabled, bool EmailEnabled);

internal static class AiProviders
{
    public sealed record Definition(string Key, string Name, string Kind, AgentRuntime Runtime, ModelProvider Provider, string DefaultModel, string Description,
        (string Name, string Label)[] Credentials, string? InstallHint = null);

    public static readonly Definition[] All =
    [
        new("claude-code", "Claude Code CLI", "cli", AgentRuntime.ClaudeCode, ModelProvider.Anthropic, "claude-sonnet-4-5",
            "Agents run `claude -p` in the work item's repositories with the repository's allowed commands.",
            [(SecretNames.ClaudeCodeOAuthToken, "Claude subscription token (claude setup-token)"), (SecretNames.AnthropicApiKey, "or Anthropic API key")],
            "npm install -g @anthropic-ai/claude-code"),
        new("cursor", "Cursor CLI", "cli", AgentRuntime.CursorCli, ModelProvider.OpenAI, "sonnet-4.5",
            "Agents run `cursor-agent -p` in the work item's repositories.", [(SecretNames.CursorApiKey, "Cursor API key")],
            "curl https://cursor.com/install -fsS | bash"),
        new("anthropic", "Anthropic API", "api", AgentRuntime.Framework, ModelProvider.Anthropic, "claude-sonnet-4-5",
            "Claude models through the platform's own agent loop.", [(SecretNames.AnthropicApiKey, "API key")]),
        new("openai", "OpenAI", "api", AgentRuntime.Framework, ModelProvider.OpenAI, "gpt-5", "GPT models through the platform's own agent loop.",
            [(SecretNames.OpenAiApiKey, "API key")]),
        new("azure-openai", "Azure OpenAI", "api", AgentRuntime.Framework, ModelProvider.AzureOpenAI, "gpt-5",
            "Your Azure OpenAI deployment (model = deployment name).", [(SecretNames.AzureOpenAiApiKey, "API key")]),
        new("gemini", "Google Gemini", "api", AgentRuntime.Framework, ModelProvider.Gemini, "gemini-2.5-pro", "Gemini models through the platform's own agent loop.",
            [(SecretNames.GeminiApiKey, "API key")]),
    ];

    public static readonly HashSet<string> CredentialNames = All.SelectMany(d => d.Credentials.Select(c => c.Name)).ToHashSet();
}

internal static class SettingsQueries
{
    public static async Task<SettingsDto> LoadAsync(IAppDbContext db, Guid organizationId, SecretResolver secrets, IAgentRuntimeCatalog runtimes,
        IExternalIdentityProvider sso, IEmailSender email, CancellationToken ct)
    {
        var organization = await db.Organizations.AsNoTracking().FirstAsync(o => o.Id == organizationId, ct);
        var configured = await secrets.ConfiguredAsync(organizationId, ct);
        var sp = configured.ContainsKey(SecretNames.ServicePrincipalTenantId) && configured.ContainsKey(SecretNames.ServicePrincipalClientId)
            && configured.ContainsKey(SecretNames.ServicePrincipalClientSecret);
        var projects = await db.Projects.AsNoTracking().CountAsync(p => !p.IsRemoved, ct);
        var repos = await db.Repositories.AsNoTracking().Where(r => !r.IsRemoved).Select(r => r.MirrorSyncedAt).ToListAsync(ct);
        var azure = new AzureConnectionDto(organization.AzureOrganizationUrl, organization.Name,
            sp ? "service-principal" : configured.ContainsKey(SecretNames.AzureDevOpsPat) ? "pat" : "none",
            configured.ContainsKey(SecretNames.AzureDevOpsPat), sp, organization.LastSyncedAt, organization.LastSyncError, projects, repos.Count,
            repos.Count(r => r != null), await db.Teams.AsNoTracking().CountAsync(ct), await db.WorkItems.AsNoTracking().CountAsync(ct),
            configured.ContainsKey(SecretNames.ServiceHookPassword));
        var installed = (await runtimes.ListAsync(ct)).ToDictionary(r => r.Runtime);
        var providers = AiProviders.All.Select(d =>
        {
            var credentials = d.Credentials.Select(c => new AiCredentialDto(c.Name, c.Label, configured.ContainsKey(c.Name), configured.TryGetValue(c.Name, out var at) ? at : null)).ToList();
            var runtime = d.Kind == "cli" ? installed.GetValueOrDefault(d.Runtime) : null;
            return new AiProviderDto(d.Key, d.Name, d.Kind, d.Runtime, d.Provider, d.DefaultModel, d.Description, credentials,
                credentials.Any(c => c.Configured) && (d.Key != "azure-openai" || organization.Settings.AzureOpenAiEndpoint is { Length: > 0 }),
                runtime?.Available, runtime?.Version, runtime is { Available: false } ? d.InstallHint : null);
        }).ToList();
        return new SettingsDto(azure, organization.Settings, organization.MonthlyBudgetUsd, providers, sso.Enabled, email.Enabled);
    }
}

public sealed record GetSettingsQuery;

public static class GetSettingsHandler
{
    public static async Task<SettingsDto> Handle(GetSettingsQuery _, IAppDbContext db, AccessControl access, SecretResolver secrets, IAgentRuntimeCatalog runtimes,
        IExternalIdentityProvider sso, IEmailSender email, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        return await SettingsQueries.LoadAsync(db, access.RequireOrganization(), secrets, runtimes, sso, email, ct);
    }
}

public sealed record ProviderStatusDto(string Key, string Name, string Kind, AgentRuntime Runtime, ModelProvider Provider, string DefaultModel, string Description,
    bool Configured, bool? Installed, string? Version);

public sealed record ProviderStatusListDto(IReadOnlyList<ProviderStatusDto> Providers, AgentRuntime DefaultRuntime, ModelProvider DefaultProvider, string DefaultModel);

/// <summary>Which AI backends are ready (no secrets), for everyone who defines agents.</summary>
public sealed record ListProviderStatusQuery;

public static class ListProviderStatusHandler
{
    public static async Task<ProviderStatusListDto> Handle(ListProviderStatusQuery _, IAppDbContext db, AccessControl access, ICurrentUser user, SecretResolver secrets,
        IAgentRuntimeCatalog runtimes, IExternalIdentityProvider sso, IEmailSender email, CancellationToken ct)
    {
        if (await access.OrganizationRoleAsync(ct) is null && !user.IsSystemAdmin) throw new ForbiddenException();
        var settings = await SettingsQueries.LoadAsync(db, access.RequireOrganization(), secrets, runtimes, sso, email, ct);
        return new ProviderStatusListDto(settings.Providers.Select(p => new ProviderStatusDto(p.Key, p.Name, p.Kind, p.Runtime, p.Provider, p.DefaultModel, p.Description,
                p.Configured, p.Installed, p.Version)).ToList(),
            settings.Automation.DefaultRuntime, settings.Automation.DefaultProvider, settings.Automation.DefaultModel);
    }
}

// Azure DevOps connection ----------------------------------------------------------------------------------

/// <summary>Sets the organization URL and the service identity (PAT or service principal), verifies them, and syncs everything.</summary>
public sealed record SaveAzureConnectionCommand(string OrganizationUrl, string? PersonalAccessToken, string? ServicePrincipalTenantId, string? ServicePrincipalClientId,
    string? ServicePrincipalClientSecret);

public sealed class SaveAzureConnectionCommandValidator : AbstractValidator<SaveAzureConnectionCommand>
{
    public SaveAzureConnectionCommandValidator()
    {
        RuleFor(x => x.OrganizationUrl).SetValidator(new AzureUrlValidator());
        RuleFor(x => x.PersonalAccessToken).Length(20, 500).Matches("^[A-Za-z0-9]+$").When(x => !string.IsNullOrEmpty(x.PersonalAccessToken))
            .WithMessage("A PAT contains letters and digits only");
        RuleFor(x => x).Must(x => string.IsNullOrEmpty(x.ServicePrincipalClientId) == string.IsNullOrEmpty(x.ServicePrincipalTenantId)
                && string.IsNullOrEmpty(x.ServicePrincipalClientId) == string.IsNullOrEmpty(x.ServicePrincipalClientSecret))
            .WithName("ServicePrincipal").WithMessage("A service principal needs tenant id, client id and client secret");
    }
}

public static class SaveAzureConnectionHandler
{
    public static async Task<(AzureConnectionDto, IEnumerable<object>)> Handle(SaveAzureConnectionCommand command, IAppDbContext db, AccessControl access, ICurrentUser user,
        SecretResolver secrets, IAzureDevOpsClientFactory factory, IAzureTokenProvider tokens, IAgentRuntimeCatalog runtimes, IExternalIdentityProvider sso,
        IEmailSender email, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        var organizationId = access.RequireOrganization();
        var organization = await db.Organizations.FirstAsync(o => o.Id == organizationId, ct);
        var url = command.OrganizationUrl.Trim().TrimEnd('/');
        if (organization.AzureOrganizationUrl is { } current && !current.Equals(url, StringComparison.OrdinalIgnoreCase) && await db.Projects.AnyAsync(ct))
            throw new BusinessRuleException("This platform organization is already connected to another Azure DevOps organization");

        AzureCredential? credential = null;
        if (!string.IsNullOrEmpty(command.ServicePrincipalClientId))
            credential = AzureCredential.Bearer((await tokens.GetServicePrincipalTokenAsync(command.ServicePrincipalTenantId!, command.ServicePrincipalClientId,
                command.ServicePrincipalClientSecret!, ct)).AccessToken, "service-principal");
        else if (!string.IsNullOrEmpty(command.PersonalAccessToken)) credential = AzureCredential.Pat(command.PersonalAccessToken);
        else if (await secrets.GetOrganizationAsync(organizationId, SecretNames.AzureDevOpsPat, ct) is { } saved) credential = AzureCredential.Pat(saved);
        if (credential is null) throw new BusinessRuleException("Enter a personal access token or a service principal");
        await AzureVerification.RequireAccessAsync(factory, url, credential, ct);

        organization.AzureOrganizationUrl = url;
        if (!string.IsNullOrEmpty(command.PersonalAccessToken))
        {
            await secrets.SetOrganizationAsync(organizationId, SecretNames.AzureDevOpsPat, command.PersonalAccessToken, ct);
            // Choosing a PAT replaces a service principal, which would otherwise keep taking precedence.
            foreach (var name in new[] { SecretNames.ServicePrincipalTenantId, SecretNames.ServicePrincipalClientId, SecretNames.ServicePrincipalClientSecret })
                await secrets.DeleteOrganizationAsync(organizationId, name, ct);
        }
        if (!string.IsNullOrEmpty(command.ServicePrincipalClientId))
        {
            await secrets.SetOrganizationAsync(organizationId, SecretNames.ServicePrincipalTenantId, command.ServicePrincipalTenantId!, ct);
            await secrets.SetOrganizationAsync(organizationId, SecretNames.ServicePrincipalClientId, command.ServicePrincipalClientId, ct);
            await secrets.SetOrganizationAsync(organizationId, SecretNames.ServicePrincipalClientSecret, command.ServicePrincipalClientSecret!, ct);
        }
        db.Audit(user, organizationId, "settings.azure.saved", "organization", organizationId, new { url, servicePrincipal = !string.IsNullOrEmpty(command.ServicePrincipalClientId) });
        await db.SaveChangesAsync(ct);
        var settings = await SettingsQueries.LoadAsync(db, organizationId, secrets, runtimes, sso, email, ct);
        return (settings.Azure, [new SyncOrganization(organizationId, "settings")]);
    }
}

public static class AzureVerification
{
    /// <summary>The credential must list the organization's projects (read access to everything sync needs starts there).</summary>
    public static async Task<IReadOnlyList<AzureProject>> RequireAccessAsync(IAzureDevOpsClientFactory factory, string organizationUrl, AzureCredential credential, CancellationToken ct)
    {
        try
        {
            return await factory.Create(new AzureConnectionInfo(organizationUrl, "", credential)).ListProjectsAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException and not AppException)
        {
            throw new BusinessRuleException($"Azure DevOps rejected the connection to {organizationUrl}: {e.Message}");
        }
    }
}

/// <summary>Starts a full sync (projects, then every project's boards, repos and people) in the background.</summary>
public sealed record SyncNowCommand;

public static class SyncNowHandler
{
    public static async Task<(string, IEnumerable<object>)> Handle(SyncNowCommand _, AccessControl access, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        return ("queued", [new SyncOrganization(access.RequireOrganization(), "manual")]);
    }
}

/// <summary>Creates (or rotates) the password Azure Service Hooks use; it is shown once.</summary>
public sealed record RotateServiceHookPasswordCommand;

public static class RotateServiceHookPasswordHandler
{
    public static async Task<string> Handle(RotateServiceHookPasswordCommand _, IAppDbContext db, AccessControl access, ICurrentUser user, SecretResolver secrets, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        var organizationId = access.RequireOrganization();
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        await secrets.SetOrganizationAsync(organizationId, SecretNames.ServiceHookPassword, password, ct);
        db.Audit(user, organizationId, "settings.service_hook_password.rotated", "organization", organizationId);
        await db.SaveChangesAsync(ct);
        return password;
    }
}

// AI providers ------------------------------------------------------------------------------------------------

public sealed record SetProviderCredentialCommand(string Name, string Value);

public sealed class SetProviderCredentialCommandValidator : AbstractValidator<SetProviderCredentialCommand>
{
    public SetProviderCredentialCommandValidator()
    {
        RuleFor(x => x.Name).Must(AiProviders.CredentialNames.Contains).WithMessage("Unknown provider credential");
        RuleFor(x => x.Value).NotEmpty().MaximumLength(10_000);
    }
}

public static class SetProviderCredentialHandler
{
    public static async Task Handle(SetProviderCredentialCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, SecretResolver secrets, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        var organizationId = access.RequireOrganization();
        await secrets.SetOrganizationAsync(organizationId, command.Name, command.Value.Trim(), ct);
        db.Audit(user, organizationId, "settings.ai_credential.set", "secret", command.Name);
        await db.SaveChangesAsync(ct);
    }
}

public sealed record DeleteProviderCredentialCommand(string Name);

public static class DeleteProviderCredentialHandler
{
    public static async Task Handle(DeleteProviderCredentialCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, SecretResolver secrets, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        if (!AiProviders.CredentialNames.Contains(command.Name)) throw new NotFoundException("Credential");
        var organizationId = access.RequireOrganization();
        if (!await secrets.DeleteOrganizationAsync(organizationId, command.Name, ct)) throw new NotFoundException("Credential");
        db.Audit(user, organizationId, "settings.ai_credential.deleted", "secret", command.Name);
        await db.SaveChangesAsync(ct);
    }
}

public sealed record ProviderTestResult(bool Ok, string Detail);

/// <summary>Checks a provider with its stored credential (model list for APIs, a one-turn prompt for CLIs).</summary>
public sealed record TestProviderCommand(string Provider);

public static class TestProviderHandler
{
    public static async Task<ProviderTestResult> Handle(TestProviderCommand command, IAppDbContext db, AccessControl access, SecretResolver secrets, IAiProviderProbe probe,
        CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        var organizationId = access.RequireOrganization();
        var definition = AiProviders.All.FirstOrDefault(d => d.Key == command.Provider) ?? throw new NotFoundException("Provider");
        var credentials = new Dictionary<string, string>();
        foreach (var (name, _) in definition.Credentials)
            if (await secrets.GetOrganizationAsync(organizationId, name, ct) is { } value) credentials[name] = value;
        if (credentials.Count == 0) return new ProviderTestResult(false, "No credential is configured");
        var endpoint = (await db.Organizations.AsNoTracking().FirstAsync(o => o.Id == organizationId, ct)).Settings.AzureOpenAiEndpoint;
        var (ok, detail) = await probe.ProbeAsync(definition.Key, credentials, endpoint, ct);
        return new ProviderTestResult(ok, detail);
    }
}

// Automation, people rules and AI defaults ------------------------------------------------------------------------

public sealed record SaveAutomationCommand(OrganizationSettings Settings, decimal? MonthlyBudgetUsd);

public sealed class SaveAutomationCommandValidator : AbstractValidator<SaveAutomationCommand>
{
    public SaveAutomationCommandValidator()
    {
        RuleFor(x => x.Settings).NotNull();
        RuleFor(x => x.Settings.SyncIntervalMinutes).InclusiveBetween(1, 1440);
        RuleFor(x => x.Settings.DefaultModel).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Settings.DefaultRuntime).IsInEnum();
        RuleFor(x => x.Settings.DefaultProvider).IsInEnum();
        RuleFor(x => x.Settings.AzureOpenAiEndpoint).Must(e => Uri.TryCreate(e, UriKind.Absolute, out var u) && u.Scheme == "https")
            .When(x => !string.IsNullOrEmpty(x.Settings.AzureOpenAiEndpoint)).WithMessage("Endpoint must be an https URL");
        RuleFor(x => x.MonthlyBudgetUsd).GreaterThanOrEqualTo(0).When(x => x.MonthlyBudgetUsd.HasValue);
        RuleForEach(x => x.Settings.TeamRoles).ChildRules(m =>
        {
            m.RuleFor(x => x.Team).NotEmpty().MaximumLength(200);
            m.RuleFor(x => x.Role).IsInEnum();
        });
        RuleForEach(x => x.Settings.GroupRoles).ChildRules(m =>
        {
            m.RuleFor(x => x.GroupId).NotEmpty().Matches("^[0-9a-fA-F-]{36}$").WithMessage("Use the Entra ID group object id");
            m.RuleFor(x => x.Role).IsInEnum();
        });
    }
}

public static class SaveAutomationHandler
{
    public static async Task<OrganizationSettings> Handle(SaveAutomationCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        var organizationId = access.RequireOrganization();
        var organization = await db.Organizations.FirstAsync(o => o.Id == organizationId, ct);
        organization.Settings = command.Settings;
        organization.MonthlyBudgetUsd = command.MonthlyBudgetUsd;
        db.Audit(user, organizationId, "settings.automation.saved", "organization", organizationId, command);
        await db.SaveChangesAsync(ct);
        return organization.Settings;
    }
}

/// <summary>Per-project monthly budget (Settings → Costs).</summary>
public sealed record SetProjectBudgetCommand(Guid ProjectId, decimal? MonthlyBudgetUsd);

public static class SetProjectBudgetHandler
{
    public static async Task Handle(SetProjectBudgetCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.BudgetsManage, ct);
        if (command.MonthlyBudgetUsd < 0) throw new BusinessRuleException("Budget cannot be negative");
        var project = await db.Projects.FirstAsync(p => p.Id == command.ProjectId, ct);
        project.MonthlyBudgetUsd = command.MonthlyBudgetUsd;
        db.Audit(user, project.OrganizationId, "project.budget.set", "project", project.Id, new { command.MonthlyBudgetUsd }, project.Id);
        await db.SaveChangesAsync(ct);
    }
}
