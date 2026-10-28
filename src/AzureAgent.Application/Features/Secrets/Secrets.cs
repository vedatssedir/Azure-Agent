using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Features.Secrets;

/// <summary>Resolves a secret for server-side use: project value first, then organization-wide.</summary>
public sealed class SecretResolver(IAppDbContext db, ISecretProtector protector)
{
    public async Task<string?> GetAsync(Guid projectId, string name, CancellationToken ct)
    {
        var secret = await db.Secrets.AsNoTracking()
            .Where(s => s.Name == name && (s.ProjectId == projectId || s.ProjectId == null))
            .OrderBy(s => s.ProjectId == null).FirstOrDefaultAsync(ct);
        return secret is null ? null : protector.Unprotect(secret.Ciphertext);
    }

    public async Task<string> RequireAsync(Guid projectId, string name, CancellationToken ct) =>
        await GetAsync(projectId, name, ct) ?? throw new BusinessRuleException($"{name} is not configured (Settings → AI providers)");

    public async Task<string?> GetOrganizationAsync(Guid organizationId, string name, CancellationToken ct)
    {
        var secret = await db.Secrets.AsNoTracking().FirstOrDefaultAsync(s => s.OrganizationId == organizationId && s.ProjectId == null && s.Name == name, ct);
        return secret is null ? null : protector.Unprotect(secret.Ciphertext);
    }

    /// <summary>Stores (or replaces) an organization-wide secret. Values are never returned by the API.</summary>
    public async Task SetOrganizationAsync(Guid organizationId, string name, string value, CancellationToken ct)
    {
        var secret = await db.Secrets.FirstOrDefaultAsync(s => s.OrganizationId == organizationId && s.ProjectId == null && s.Name == name, ct);
        if (secret is null) db.Secrets.Add(new Domain.Projects.Secret { OrganizationId = organizationId, Name = name, Ciphertext = protector.Protect(value) });
        else secret.Ciphertext = protector.Protect(value);
    }

    public async Task<bool> DeleteOrganizationAsync(Guid organizationId, string name, CancellationToken ct)
    {
        var secret = await db.Secrets.FirstOrDefaultAsync(s => s.OrganizationId == organizationId && s.ProjectId == null && s.Name == name, ct);
        if (secret is null) return false;
        db.Secrets.Remove(secret);
        return true;
    }

    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> ConfiguredAsync(Guid organizationId, CancellationToken ct) =>
        await db.Secrets.AsNoTracking().Where(s => s.OrganizationId == organizationId && s.ProjectId == null).ToDictionaryAsync(s => s.Name, s => s.UpdatedAt, ct);
}
