using AzureAgent.Application.Abstractions;
using AzureAgent.Domain.Agents;
using AzureAgent.Domain.Costs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzureAgent.Infrastructure.Persistence;

/// <summary>Applies migrations and seeds instance-default model prices (USD per 1M tokens).</summary>
public sealed class DatabaseInitializer(AppDbContext db, ITenantContext tenant, ILogger<DatabaseInitializer> logger)
{
    // Defaults are a starting point; organizations override them under Settings → Model prices.
    private static readonly ModelPrice[] DefaultPrices =
    [
        new() { Provider = ModelProvider.OpenAI, Model = "gpt-5", InputPerMillion = 1.25m, OutputPerMillion = 10m, CachedInputPerMillion = 0.125m },
        new() { Provider = ModelProvider.OpenAI, Model = "gpt-5-mini", InputPerMillion = 0.25m, OutputPerMillion = 2m, CachedInputPerMillion = 0.025m },
        new() { Provider = ModelProvider.OpenAI, Model = "gpt-4.1", InputPerMillion = 2m, OutputPerMillion = 8m, CachedInputPerMillion = 0.5m },
        new() { Provider = ModelProvider.AzureOpenAI, Model = "gpt-5", InputPerMillion = 1.25m, OutputPerMillion = 10m, CachedInputPerMillion = 0.125m },
        new() { Provider = ModelProvider.Anthropic, Model = "claude-sonnet", InputPerMillion = 3m, OutputPerMillion = 15m, CachedInputPerMillion = 0.3m },
        new() { Provider = ModelProvider.Anthropic, Model = "claude-opus", InputPerMillion = 5m, OutputPerMillion = 25m, CachedInputPerMillion = 0.5m },
        new() { Provider = ModelProvider.Anthropic, Model = "claude-haiku", InputPerMillion = 1m, OutputPerMillion = 5m, CachedInputPerMillion = 0.1m },
        new() { Provider = ModelProvider.Gemini, Model = "gemini-2.5-pro", InputPerMillion = 1.25m, OutputPerMillion = 10m, CachedInputPerMillion = 0.31m },
        new() { Provider = ModelProvider.Gemini, Model = "gemini-2.5-flash", InputPerMillion = 0.3m, OutputPerMillion = 2.5m, CachedInputPerMillion = 0.075m },
    ];

    public async Task InitializeAsync(CancellationToken ct)
    {
        await db.Database.MigrateAsync(ct);
        tenant.UseAllOrganizations();
        var existing = await db.ModelPrices.Where(p => p.OrganizationId == null).Select(p => new { p.Provider, p.Model }).ToListAsync(ct);
        var missing = DefaultPrices.Where(d => !existing.Any(e => e.Provider == d.Provider && e.Model == d.Model)).ToList();
        if (missing.Count == 0) return;
        db.ModelPrices.AddRange(missing.Select(d => new ModelPrice
        {
            Provider = d.Provider, Model = d.Model, InputPerMillion = d.InputPerMillion, OutputPerMillion = d.OutputPerMillion, CachedInputPerMillion = d.CachedInputPerMillion,
        }));
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded {Count} default model prices", missing.Count);
    }
}
