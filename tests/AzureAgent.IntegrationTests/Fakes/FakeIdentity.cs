using System.Collections.Concurrent;
using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Common;

namespace AzureAgent.IntegrationTests.Fakes;

/// <summary>Entra ID stand-in: tests register the identity a sign-in code resolves to.</summary>
public sealed class FakeIdentityProvider : IExternalIdentityProvider
{
    public ConcurrentDictionary<string, ExternalIdentity> Codes { get; } = new();
    private readonly ConcurrentDictionary<string, bool> _states = new();

    public bool Enabled => true;
    public string Name => "Microsoft";

    public Task<string> BeginAsync(CancellationToken ct)
    {
        var state = Guid.NewGuid().ToString("N");
        _states[state] = true;
        return Task.FromResult($"https://login.test/authorize?state={state}");
    }

    public Task<ExternalIdentity> CompleteAsync(string code, string state, CancellationToken ct)
    {
        if (!_states.TryRemove(state, out _)) throw new UnauthorizedException("The sign-in expired; start again");
        return Codes.TryGetValue(code, out var identity) ? Task.FromResult(identity) : throw new UnauthorizedException("Invalid code");
    }
}

public sealed class FakeTokenProvider : IAzureTokenProvider
{
    public ConcurrentQueue<string> Requests { get; } = new();

    public Task<OAuthToken> GetServicePrincipalTokenAsync(string tenantId, string clientId, string clientSecret, CancellationToken ct)
    {
        Requests.Enqueue($"sp:{tenantId}:{clientId}");
        return Task.FromResult(new OAuthToken($"sp-token-{clientId}", null, DateTimeOffset.UtcNow.AddHours(1)));
    }

    public Task<OAuthToken> RefreshUserTokenAsync(string refreshToken, CancellationToken ct)
    {
        Requests.Enqueue($"refresh:{refreshToken}");
        return Task.FromResult(new OAuthToken($"refreshed-{refreshToken}", $"{refreshToken}-next", DateTimeOffset.UtcNow.AddHours(1)));
    }
}
