using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Application.Features.Auth;
using AzureAgent.Domain.Identity;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Application.Features.Organizations;

public sealed record CreateOrganizationCommand(string Name, decimal? MonthlyBudgetUsd);

public sealed class CreateOrganizationCommandValidator : AbstractValidator<CreateOrganizationCommand>
{
    public CreateOrganizationCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.MonthlyBudgetUsd).GreaterThanOrEqualTo(0).When(x => x.MonthlyBudgetUsd.HasValue);
    }
}

public static class CreateOrganizationHandler
{
    public static async Task<OrganizationMembershipDto> Handle(CreateOrganizationCommand command, IAppDbContext db, ICurrentUser user,
        ITenantContext tenant, CancellationToken ct)
    {
        var userId = user.UserId ?? throw new UnauthorizedException();
        if (!user.IsSystemAdmin) throw new ForbiddenException("Only system administrators can create organizations");
        tenant.UseAllOrganizations();
        var slug = Slugs.From(command.Name);
        if (await db.Organizations.AnyAsync(o => o.Slug == slug, ct)) slug = $"{slug}-{Guid.NewGuid().ToString("N")[..6]}";
        var organization = new Organization { Name = command.Name.Trim(), Slug = slug, MonthlyBudgetUsd = command.MonthlyBudgetUsd };
        db.Organizations.Add(organization);
        db.OrganizationMembers.Add(new OrganizationMember { OrganizationId = organization.Id, UserId = userId, Role = OrganizationRole.Owner });
        db.Audit(user, organization.Id, "organization.created", "organization", organization.Id);
        await db.SaveChangesAsync(ct);
        return new OrganizationMembershipDto(organization.Id, organization.Name, organization.Slug, OrganizationRole.Owner);
    }
}

public sealed record OrganizationMemberDto(Guid UserId, string Email, string DisplayName, OrganizationRole Role, bool IsActive);

public sealed record ListOrganizationMembersQuery;

public static class ListOrganizationMembersHandler
{
    public static async Task<IReadOnlyList<OrganizationMemberDto>> Handle(ListOrganizationMembersQuery _, IAppDbContext db, AccessControl access, CancellationToken ct)
    {
        if (await access.OrganizationRoleAsync(ct) is null) throw new ForbiddenException();
        return await db.OrganizationMembers.AsNoTracking()
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m, u })
            .OrderBy(x => x.u.DisplayName)
            .Select(x => new OrganizationMemberDto(x.u.Id, x.u.Email, x.u.DisplayName, x.m.Role, x.u.IsActive)).ToListAsync(ct);
    }
}

/// <summary>Creates the user if needed (with an initial password) and adds them to the organization.</summary>
public sealed record AddOrganizationMemberCommand(string Email, string DisplayName, OrganizationRole Role, string? InitialPassword);

public sealed class AddOrganizationMemberCommandValidator : AbstractValidator<AddOrganizationMemberCommand>
{
    public AddOrganizationMemberCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Role).IsInEnum();
        RuleFor(x => x.InitialPassword!).SetValidator(new PasswordValidator()).When(x => x.InitialPassword is not null);
    }
}

public static class AddOrganizationMemberHandler
{
    public static async Task<OrganizationMemberDto> Handle(AddOrganizationMemberCommand command, IAppDbContext db, AccessControl access,
        ICurrentUser current, IPasswordHasher hasher, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        var organizationId = access.RequireOrganization();
        if (command.Role == OrganizationRole.Owner && await access.OrganizationRoleAsync(ct) != OrganizationRole.Owner && !current.IsSystemAdmin)
            throw new ForbiddenException("Only owners can add owners");

        var email = command.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null)
        {
            if (command.InitialPassword is null) throw new BusinessRuleException("An initial password is required for new users");
            user = new User { Email = email, DisplayName = command.DisplayName.Trim(), PasswordHash = hasher.Hash(command.InitialPassword) };
            db.Users.Add(user);
        }
        var member = await db.OrganizationMembers.FirstOrDefaultAsync(m => m.UserId == user.Id, ct);
        if (member is null) db.OrganizationMembers.Add(new OrganizationMember { OrganizationId = organizationId, UserId = user.Id, Role = command.Role });
        else member.Role = command.Role;
        db.Audit(current, organizationId, "organization.member.saved", "user", user.Id, new { email, command.Role });
        await db.SaveChangesAsync(ct);
        return new OrganizationMemberDto(user.Id, user.Email, user.DisplayName, command.Role, user.IsActive);
    }
}

public sealed record SetUserActiveCommand(Guid UserId, bool IsActive);

public static class SetUserActiveHandler
{
    public static async Task Handle(SetUserActiveCommand command, IAppDbContext db, AccessControl access, ICurrentUser current, IClock clock, CancellationToken ct)
    {
        await access.RequireOrganizationAdminAsync(ct);
        var organizationId = access.RequireOrganization();
        if (command.UserId == current.UserId) throw new BusinessRuleException("You cannot deactivate yourself");
        if (!await db.OrganizationMembers.AnyAsync(m => m.UserId == command.UserId, ct)) throw new NotFoundException("Member");
        var user = await db.Users.FirstAsync(u => u.Id == command.UserId, ct);
        user.IsActive = command.IsActive;
        if (!command.IsActive)
        {
            foreach (var token in await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null).ToListAsync(ct)) token.RevokedAt = clock.UtcNow;
        }
        db.Audit(current, organizationId, command.IsActive ? "user.activated" : "user.deactivated", "user", user.Id);
        await db.SaveChangesAsync(ct);
    }
}
