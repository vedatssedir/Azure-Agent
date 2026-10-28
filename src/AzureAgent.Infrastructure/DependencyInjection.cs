using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Application.Features.Costs;
using AzureAgent.Application.Features.Identity;
using AzureAgent.Application.Features.Secrets;
using AzureAgent.Infrastructure.AgentRuntime;
using AzureAgent.Infrastructure.AzureDevOps;
using AzureAgent.Infrastructure.Caching;
using AzureAgent.Infrastructure.Identity;
using AzureAgent.Infrastructure.Persistence;
using AzureAgent.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AzureAgent.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var postgres = configuration.GetConnectionString("Postgres") ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required");

        services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.Section))
            .Validate(o => o.SigningKey.Length >= 32, "Jwt:SigningKey must be at least 32 characters").ValidateOnStart();
        services.AddOptions<EncryptionOptions>().Bind(configuration.GetSection(EncryptionOptions.Section))
            .Validate(o => o.Key.Length > 0, "Encryption:Key is required").ValidateOnStart();
        services.AddOptions<AgentRuntimeOptions>().Bind(configuration.GetSection(AgentRuntimeOptions.Section));
        services.AddOptions<EntraOptions>().Bind(configuration.GetSection(EntraOptions.Section))
            .Validate(o => !o.Problems().Any(), "Entra configuration is incomplete (ClientId, ClientSecret, RedirectUri, AllowedTenantIds for multi-tenant)")
            .ValidateOnStart();
        services.AddOptions<SmtpOptions>().Bind(configuration.GetSection(SmtpOptions.Section));
        services.AddOptions<AppLinkOptions>().Bind(configuration.GetSection(AppLinkOptions.Section));

        services.AddHttpContextAccessor();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(postgres, npgsql =>
        {
            npgsql.MigrationsHistoryTable("__ef_migrations", "app");
            npgsql.EnableRetryOnFailure(3);
        }));
        // A plain type mapping (not a lambda) so Wolverine can inline it in generated handler code.
        services.AddScoped<IAppDbContext, AppDbContext>();

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPasswordHasher, BcryptPasswordHasher>();
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddScoped<ITenantContext, TenantContext>();

        if (configuration.GetConnectionString("Redis") is { Length: > 0 } redis)
        {
            services.AddStackExchangeRedisCache(o =>
            {
                o.Configuration = redis;
                o.InstanceName = "azure-agent:";
            });
        }
        else
        {
            services.AddDistributedMemoryCache();
        }
        services.AddSingleton<ICacheService, DistributedCacheService>();

        services.AddMemoryCache();
        services.AddHttpClient(AzureDevOpsClientFactory.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(100))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient(EntraTokenProvider.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<EntraTokenProvider>();
        services.AddSingleton<IAzureTokenProvider>(sp => sp.GetRequiredService<EntraTokenProvider>());
        if (configuration.GetValue("Entra:Enabled", false)) services.AddSingleton<IExternalIdentityProvider, EntraIdentityProvider>();
        else services.AddSingleton<IExternalIdentityProvider, DisabledIdentityProvider>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<IAppLinks, AppLinks>();
        services.AddHttpClient("anthropic", c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddSingleton<IAzureDevOpsClientFactory, AzureDevOpsClientFactory>();
        services.AddSingleton<IChatClientFactory, ChatClientFactory>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();

        // Application services used by Wolverine handlers.
        services.AddScoped<AccessControl>();
        services.AddScoped<SecretResolver>();
        services.AddScoped<AzureConnector>();
        services.AddScoped<UserAzureCredentials>();
        services.AddScoped<InvitationService>();
        services.AddScoped<MembershipProvisioner>();
        services.AddScoped<Dispatcher>();
        services.AddScoped<ProjectSynchronizer>();
        services.AddScoped<BudgetGuard>();
        services.AddScoped<PriceList>();
        services.AddScoped<McpToolLoader>();
        services.AddScoped<AgentRunExecutor>();
        services.AddScoped<Jobs.ScheduledJobs>();
        services.AddScoped<DatabaseInitializer>();
        return services;
    }
}
