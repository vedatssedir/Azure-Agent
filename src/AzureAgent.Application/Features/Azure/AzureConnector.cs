using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Application.Features.Identity;
using AzureAgent.Application.Features.Secrets;
using AzureAgent.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Features.Azure;

/// <summary>
/// Opens Azure DevOps clients for a project. The service identity (a service principal when its
/// three secrets are set, otherwise the PAT) reads and syncs; writes a person starts run with that
/// person's own credential when they have connected one.
/// </summary>
public sealed class AzureConnector(IAppDbContext db, SecretResolver secrets, IAzureDevOpsClientFactory factory, IAzureTokenProvider tokens,
    UserAzureCredentials userCredentials)
{
    public async Task<AzureCredential> ServiceCredentialAsync(Guid projectId, CancellationToken ct)
    {
        var tenantId = await secrets.GetAsync(projectId, SecretNames.ServicePrincipalTenantId, ct);
        var clientId = await secrets.GetAsync(projectId, SecretNames.ServicePrincipalClientId, ct);
        var clientSecret = await secrets.GetAsync(projectId, SecretNames.ServicePrincipalClientSecret, ct);
        if (tenantId is { Length: > 0 } && clientId is { Length: > 0 } && clientSecret is { Length: > 0 })
        {
            var token = await tokens.GetServicePrincipalTokenAsync(tenantId, clientId, clientSecret, ct);
            return AzureCredential.Bearer(token.AccessToken, "service-principal");
        }
        var pat = await secrets.GetAsync(projectId, SecretNames.AzureDevOpsPat, ct)
            ?? throw new BusinessRuleException($"Configure the {SecretNames.AzureDevOpsPat} secret or a service principal ({SecretNames.ServicePrincipalTenantId}, {SecretNames.ServicePrincipalClientId}, {SecretNames.ServicePrincipalClientSecret})");
        return AzureCredential.Pat(pat);
    }

    public async Task<(Project Project, IAzureDevOpsClient Client)> ConnectAsync(Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct) ?? throw new NotFoundException("Project");
        var credential = await ServiceCredentialAsync(projectId, ct);
        return (project, factory.Create(new AzureConnectionInfo(project.AzureOrganizationUrl, project.AzureProjectName, credential)));
    }

    /// <summary>
    /// Client acting as the given user. Falls back to the service identity unless the project
    /// requires personal credentials for writes.
    /// </summary>
    public async Task<(Project Project, IAzureDevOpsClient Client, bool AsUser)> ConnectAsUserAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct) ?? throw new NotFoundException("Project");
        var personal = await userCredentials.ResolveAsync(userId, project.AzureOrganizationUrl, ct);
        if (personal is null && project.Settings.RequireUserCredentialForWrites)
            throw new BusinessRuleException("Connect your Azure DevOps account first (My account → Azure DevOps connection)");
        var credential = personal ?? await ServiceCredentialAsync(projectId, ct);
        var client = factory.Create(new AzureConnectionInfo(project.AzureOrganizationUrl, project.AzureProjectName, credential));
        return (project, client, personal is not null);
    }
}
