using System.Text.Json;
using AzureAgent.Application.Abstractions;
using Microsoft.Extensions.Caching.Distributed;

namespace AzureAgent.Infrastructure.Caching;

/// <summary>JSON cache over IDistributedCache (Redis in production, in-memory when Redis is not configured).</summary>
public sealed class DistributedCacheService(IDistributedCache cache) : ICacheService
{
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var bytes = await cache.GetAsync(key, ct);
        return bytes is null ? default : JsonSerializer.Deserialize<T>(bytes);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) =>
        cache.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(value), new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }, ct);

    public Task RemoveAsync(string key, CancellationToken ct = default) => cache.RemoveAsync(key, ct);
}
