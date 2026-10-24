using AzureAgent.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AzureAgent.Infrastructure.Persistence;

/// <summary>Used by `dotnet ef` to build the model without starting the API.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres") ?? "Host=localhost;Database=azure_agent;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connection, o => o.MigrationsHistoryTable("__ef_migrations", "app")).Options;
        return new AppDbContext(options, new TenantContext(new HttpContextAccessor()), new SystemClock());
    }
}
