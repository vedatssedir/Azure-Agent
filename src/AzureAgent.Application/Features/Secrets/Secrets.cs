using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Domain.Projects;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Features.Secrets;

public sealed record SecretDto(string Name, Guid? ProjectId, DateTimeOffset UpdatedAt);

public sealed record ListSecretsQuery(Guid ProjectId);

public static class ListSecretsHandler
{
    /// <summary>Names only: secret values never leave the server.</summary>
    public static async Task<IReadOnlyList<SecretDto>> Handle(ListSecretsQuery query, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        await access.RequireAsync(query.ProjectId, Permission.SecretsManage, ct);
        return await db.Secrets.AsNoTracking().Where(s => s.ProjectId == query.ProjectId || s.ProjectId == null)
            .OrderBy(s => s.Name).Select(s => new SecretDto(s.Name, s.ProjectId, s.UpdatedAt)).ToListAsync(ct);
    }
}

public sealed record SetSecretCommand(Guid ProjectId, string Name, string Value);

public sealed class SetSecretCommandValidator : AbstractValidator<SetSecretCommand>
{
    public SetSecretCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().Matches("^[A-Z][A-Z0-9_]{1,79}$").WithMessage("Use UPPER_SNAKE_CASE names");
        RuleFor(x => x.Value).NotEmpty().MaximumLength(10_000);
    }
}

public static class SetSecretHandler
{
    public static async Task Handle(SetSecretCommand command, IAppDbContext db, AccessControl access, ISecretProtector protector, ICurrentUser user, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.SecretsManage, ct);
        var organizationId = access.RequireOrganization();
        var secret = await db.Secrets.FirstOrDefaultAsync(s => s.ProjectId == command.ProjectId && s.Name == command.Name, ct);
        if (secret is null) db.Secrets.Add(new Domain.Projects.Secret { OrganizationId = organizationId, ProjectId = command.ProjectId, Name = command.Name, Ciphertext = protector.Protect(command.Value) });
        else secret.Ciphertext = protector.Protect(command.Value);
        db.Audit(user, organizationId, "secret.set", "secret", command.Name, null, command.ProjectId);
        await db.SaveChangesAsync(ct);
    }
}

public sealed record DeleteSecretCommand(Guid ProjectId, string Name);

public static class DeleteSecretHandler
{
    public static async Task Handle(DeleteSecretCommand command, IAppDbContext db, AccessControl access, ICurrentUser user, CancellationToken ct)
    {
        await access.RequireAsync(command.ProjectId, Permission.SecretsManage, ct);
        var secret = await db.Secrets.FirstOrDefaultAsync(s => s.ProjectId == command.ProjectId && s.Name == command.Name, ct) ?? throw new NotFoundException("Secret");
        db.Secrets.Remove(secret);
        db.Audit(user, secret.OrganizationId, "secret.deleted", "secret", command.Name, null, command.ProjectId);
        await db.SaveChangesAsync(ct);
    }
}

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
        await GetAsync(projectId, name, ct) ?? throw new BusinessRuleException($"Secret {name} is not configured");
}
