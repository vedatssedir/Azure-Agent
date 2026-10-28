using System.Linq.Expressions;
using AzureAgent.Application.Abstractions;
using AzureAgent.Domain.Agents;
using AzureAgent.Domain.Auditing;
using AzureAgent.Domain.Azure;
using AzureAgent.Domain.Common;
using AzureAgent.Domain.Costs;
using AzureAgent.Domain.Identity;
using AzureAgent.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace AzureAgent.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant, IClock clock) : DbContext(options), IAppDbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<UserAzureCredential> UserAzureCredentials => Set<UserAzureCredential>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<Secret> Secrets => Set<Secret>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Repository> Repositories => Set<Repository>();
    public DbSet<RepositoryAccessRule> RepositoryAccessRules => Set<RepositoryAccessRule>();
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<Commit> Commits => Set<Commit>();
    public DbSet<PullRequest> PullRequests => Set<PullRequest>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<AgentRunStep> AgentRunSteps => Set<AgentRunStep>();
    public DbSet<CodeReview> CodeReviews => Set<CodeReview>();
    public DbSet<ModelPrice> ModelPrices => Set<ModelPrice>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    // Read by the query filters at execution time, so one model serves every tenant.
    private Guid? CurrentOrganizationId => tenant.OrganizationId;
    private bool BypassTenantFilter => tenant.BypassFilter;

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("app");

        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.DisplayName).HasMaxLength(100);
        });
        b.Entity<RefreshToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ExternalLogin>(e =>
        {
            e.HasIndex(x => new { x.Provider, x.Subject }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Provider).HasMaxLength(32);
            e.Property(x => x.Subject).HasMaxLength(200);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Invitation>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.Email });
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.DisplayName).HasMaxLength(100);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<UserAzureCredential>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.Kind, x.AzureOrganizationUrl }).IsUnique().AreNullsDistinct(false);
            e.Property(x => x.AzureOrganizationUrl).HasMaxLength(300);
            e.Property(x => x.AzureUniqueName).HasMaxLength(256);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Organization>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.MonthlyBudgetUsd).HasPrecision(12, 2);
            e.OwnsOne(x => x.Settings, s =>
            {
                s.ToJson();
                s.OwnsMany(x => x.TeamRoles);
                s.OwnsMany(x => x.GroupRoles);
            });
        });
        b.Entity<OrganizationMember>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.UserId }).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Project>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.Key }).IsUnique();
            e.HasIndex(x => new { x.OrganizationId, x.AzureId }).IsUnique();
            e.OwnsMany(x => x.WorkItemTypes, t =>
            {
                t.ToJson();
                t.OwnsMany(x => x.States);
            });
            e.Property(x => x.MonthlyBudgetUsd).HasPrecision(12, 2);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ProjectMember>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.UserId }).IsUnique();
            e.HasIndex(x => new { x.ProjectId, x.AzureUniqueName });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Secret>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.ProjectId, x.Name }).IsUnique().AreNullsDistinct(false);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Team>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.AzureId }).IsUnique();
            e.OwnsMany(x => x.Members, m => m.ToJson());
            e.OwnsMany(x => x.Areas, a => a.ToJson());
            e.OwnsMany(x => x.Boards, board =>
            {
                board.ToJson();
                board.OwnsMany(x => x.Columns, c => c.OwnsMany(x => x.StateMappings));
            });
            e.OwnsMany(x => x.Iterations, i => i.ToJson());
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Repository>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.AzureId }).IsUnique();
            e.HasIndex(x => new { x.ProjectId, x.Name });
            e.OwnsOne(x => x.Policy, p => p.ToJson());
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<RepositoryAccessRule>(e =>
        {
            e.HasIndex(x => x.RepositoryId);
            e.Property(x => x.Permission).HasMaxLength(64);
            e.HasOne<Repository>().WithMany().HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<WorkItem>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.AzureId }).IsUnique();
            e.HasIndex(x => new { x.ProjectId, x.AssignedToUniqueName });
            e.HasIndex(x => new { x.ProjectId, x.ChangedAt });
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Commit>(e =>
        {
            e.HasIndex(x => new { x.RepositoryId, x.CommitId }).IsUnique();
            e.HasIndex(x => new { x.RepositoryId, x.CommittedAt });
            e.HasIndex(x => x.WorkItemAzureIds).HasMethod("gin");
            e.HasOne<Repository>().WithMany().HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<PullRequest>(e =>
        {
            e.HasIndex(x => new { x.RepositoryId, x.AzureId }).IsUnique();
            e.HasIndex(x => x.WorkItemAzureIds).HasMethod("gin");
            e.HasOne<Repository>().WithMany().HasForeignKey(x => x.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Agent>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.Slug }).IsUnique();
            e.HasIndex(x => x.OwnerUserId);
            e.Property(x => x.MonthlyBudgetUsd).HasPrecision(12, 2);
            e.OwnsOne(x => x.Settings, s =>
            {
                s.ToJson();
                s.Property(x => x.MaxCostPerRunUsd).HasPrecision(12, 4);
            });
            e.OwnsMany(x => x.Skills, s => s.ToJson());
            e.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<AgentRun>(e =>
        {
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.ProjectId, x.CreatedAt });
            e.HasIndex(x => new { x.AgentId, x.CreatedAt });
            e.HasIndex(x => x.WorkItemId);
            // At most one active run per work item and kind.
            e.HasIndex(x => new { x.WorkItemId, x.Kind }).IsUnique().HasFilter("\"Status\" IN (0, 1, 2) AND \"WorkItemId\" IS NOT NULL");
            e.Property(x => x.CostUsd).HasPrecision(14, 6);
            e.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AgentRunStep>(e =>
        {
            e.HasIndex(x => new { x.RunId, x.Sequence }).IsUnique();
            e.Property(x => x.CostUsd).HasPrecision(14, 6);
            e.HasOne<AgentRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<CodeReview>(e =>
        {
            e.HasIndex(x => x.PullRequestId);
            e.OwnsMany(x => x.Findings, f => f.ToJson());
            e.HasOne<PullRequest>().WithMany().HasForeignKey(x => x.PullRequestId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ModelPrice>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.Provider, x.Model }).IsUnique().AreNullsDistinct(false);
            e.Property(x => x.InputPerMillion).HasPrecision(12, 4);
            e.Property(x => x.OutputPerMillion).HasPrecision(12, 4);
            e.Property(x => x.CachedInputPerMillion).HasPrecision(12, 4);
        });
        b.Entity<AuditEntry>(e =>
        {
            e.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            e.Property(x => x.Details).HasColumnType("jsonb");
        });

        // Tenant isolation: every organization-scoped entity is filtered by the current organization.
        foreach (var entityType in b.Model.GetEntityTypes().Where(t => typeof(IOrganizationScoped).IsAssignableFrom(t.ClrType) && !t.IsOwned()))
        {
            var parameter = Expression.Parameter(entityType.ClrType, "e");
            var body = Expression.OrElse(
                Expression.Property(Expression.Constant(this), nameof(BypassTenantFilter)),
                Expression.Equal(
                    Expression.Convert(Expression.Property(parameter, nameof(IOrganizationScoped.OrganizationId)), typeof(Guid?)),
                    Expression.Property(Expression.Constant(this), nameof(CurrentOrganizationId))));
            b.Entity(entityType.ClrType).HasQueryFilter(Expression.Lambda(body, parameter));
        }
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
                // Writes must target the current tenant (defense in depth on top of the filters).
                if (entry.Entity is IOrganizationScoped scoped && !tenant.BypassFilter && tenant.OrganizationId is { } current && scoped.OrganizationId != current)
                    throw new InvalidOperationException("Cross-organization write rejected");
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}
