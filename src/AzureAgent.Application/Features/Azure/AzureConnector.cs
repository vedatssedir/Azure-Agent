using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Application.Features.Identity;
using AzureAgent.Application.Features.Secrets;
using AzureAgent.Domain.Identity;
using AzureAgent.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Features.Azure;

/// <summary>
/// Opens Azure DevOps clients. The organization's service identity (a service principal when its
/// three secrets are set, otherwise the PAT given at setup) reads and syncs; writes a person starts
/// run with that person's own credential when they have connected one.
/// </summary>
public sealed class AzureConnector(IAppDbContext db, SecretResolver secrets, IAzureDevOpsClientFactory factory, IAzureTokenProvider tokens,
    UserAzureCredentials userCredentials)
{
    /// <summary>Organization-wide secret (project-specific secrets are not used any more).</summary>
    public Task<string?> SecretAsync(Guid organizationId, string name, CancellationToken ct) => secrets.GetOrganizationAsync(organizationId, name, ct);

    public async Task<AzureCredential> ServiceCredentialAsync(Guid organizationId, CancellationToken ct)
    {
        var tenantId = await SecretAsync(organizationId, SecretNames.ServicePrincipalTenantId, ct);
        var clientId = await SecretAsync(organizationId, SecretNames.ServicePrincipalClientId, ct);
        var clientSecret = await SecretAsync(organizationId, SecretNames.ServicePrincipalClientSecret, ct);
        if (tenantId is { Length: > 0 } && clientId is { Length: > 0 } && clientSecret is { Length: > 0 })
        {
            var token = await tokens.GetServicePrincipalTokenAsync(tenantId, clientId, clientSecret, ct);
            return AzureCredential.Bearer(token.AccessToken, "service-principal");
        }
        var pat = await SecretAsync(organizationId, SecretNames.AzureDevOpsPat, ct)
            ?? throw new BusinessRuleException("The Azure DevOps connection is not configured (Settings → Azure DevOps)");
        return AzureCredential.Pat(pat);
    }

    public async Task<Organization> OrganizationAsync(Guid organizationId, CancellationToken ct) =>
        await db.Organizations.FirstOrDefaultAsync(o => o.Id == organizationId, ct) ?? throw new NotFoundException("Organization");

    /// <summary>Organization-level client (project list).</summary>
    public async Task<IAzureDevOpsClient> ConnectOrganizationAsync(Guid organizationId, CancellationToken ct)
    {
        var organization = await OrganizationAsync(organizationId, ct);
        if (organization.AzureOrganizationUrl is null) throw new BusinessRuleException("The Azure DevOps organization URL is not configured");
        return factory.Create(new AzureConnectionInfo(organization.AzureOrganizationUrl, "", await ServiceCredentialAsync(organizationId, ct)));
    }

    public async Task<(Project Project, IAzureDevOpsClient Client)> ConnectAsync(Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct) ?? throw new NotFoundException("Project");
        var credential = await ServiceCredentialAsync(project.OrganizationId, ct);
        return (project, factory.Create(new AzureConnectionInfo(project.AzureOrganizationUrl, project.AzureProjectName, credential)));
    }

    /// <summary>
    /// Client acting as the given user. Falls back to the service identity unless the organization
    /// requires personal credentials for writes.
    /// </summary>
    public async Task<(Project Project, IAzureDevOpsClient Client, bool AsUser)> ConnectAsUserAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct) ?? throw new NotFoundException("Project");
        var settings = (await OrganizationAsync(project.OrganizationId, ct)).Settings;
        var personal = await userCredentials.ResolveAsync(userId, project.AzureOrganizationUrl, ct);
        if (personal is null && settings.RequireUserCredentialForWrites)
            throw new BusinessRuleException("Connect your Azure DevOps account first (My account → Azure DevOps connection)");
        var credential = personal ?? await ServiceCredentialAsync(project.OrganizationId, ct);
        var client = factory.Create(new AzureConnectionInfo(project.AzureOrganizationUrl, project.AzureProjectName, credential));
        return (project, client, personal is not null);
    }

    /// <summary>
    /// Runs a write as the user. When Azure rejects the personal credential (expired, revoked or
    /// missing the Work Items write scope) the write is retried with the service identity, unless the
    /// organization requires personal credentials for writes.
    /// </summary>
    public async Task<(Project Project, T Result, bool AsUser)> WriteAsUserAsync<T>(Guid projectId, Guid userId,
        Func<IAzureDevOpsClient, bool, Task<T>> write, CancellationToken ct)
    {
        var (project, client, asUser) = await ConnectAsUserAsync(projectId, userId, ct);
        try
        {
            return (project, await write(client, asUser), asUser);
        }
        catch (AzureRequestException e) when (asUser && e.IsAuthFailure)
        {
            if ((await OrganizationAsync(project.OrganizationId, ct)).Settings.RequireUserCredentialForWrites)
                throw new BusinessRuleException($"Azure DevOps rejected your Azure connection (HTTP {e.Status}). Reconnect it with the Work Items (Read & write) scope (My account → Azure DevOps connection)");
            var service = factory.Create(new AzureConnectionInfo(project.AzureOrganizationUrl, project.AzureProjectName,
                await ServiceCredentialAsync(project.OrganizationId, ct)));
            return (project, await write(service, false), false);
        }
    }

    public Task<(Project Project, T Result, bool AsUser)> WriteAsUserAsync<T>(Guid projectId, Guid userId, Func<IAzureDevOpsClient, Task<T>> write,
        CancellationToken ct) => WriteAsUserAsync(projectId, userId, (azure, _) => write(azure), ct);
}
