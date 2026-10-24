using System.ClientModel;
using Anthropic.SDK;
using AzureAgent.Domain.Agents;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AzureAgent.Infrastructure.AgentRuntime;

public interface IChatClientFactory
{
    IChatClient Create(ModelProvider provider, string model, string apiKey, string? endpoint);
}

/// <summary>
/// Builds a Microsoft.Extensions.AI chat client per provider. OpenAI, Azure OpenAI (v1 API) and
/// Gemini (OpenAI-compatible endpoint) share the OpenAI SDK; Anthropic uses Anthropic.SDK.
/// </summary>
public sealed class ChatClientFactory(IHttpClientFactory httpClients) : IChatClientFactory
{
    public const string GeminiOpenAiEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/";

    public IChatClient Create(ModelProvider provider, string model, string apiKey, string? endpoint) => provider switch
    {
        ModelProvider.OpenAI => OpenAi(model, apiKey, endpoint),
        ModelProvider.AzureOpenAI => OpenAi(model, apiKey, $"{endpoint!.TrimEnd('/')}/openai/v1/"),
        ModelProvider.Gemini => OpenAi(model, apiKey, endpoint ?? GeminiOpenAiEndpoint),
        ModelProvider.Anthropic => new AnthropicClient(new APIAuthentication(apiKey), httpClients.CreateClient("anthropic")).Messages
            .AsBuilder().ConfigureOptions(o => o.ModelId ??= model).Build(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    private static IChatClient OpenAi(string model, string apiKey, string? endpoint)
    {
        var options = new OpenAIClientOptions { NetworkTimeout = TimeSpan.FromMinutes(5) };
        if (endpoint is not null) options.Endpoint = new Uri(endpoint);
        return new OpenAIClient(new ApiKeyCredential(apiKey), options).GetChatClient(model).AsIChatClient();
    }
}

/// <summary>Usage of one model call.</summary>
public sealed record ModelCallUsage(long InputTokens, long OutputTokens, long CachedInputTokens, TimeSpan Duration, int ToolCallsRequested);

/// <summary>
/// Sits under function invocation so it sees every model round trip. Reports usage per call; the
/// callback can throw to stop the run when token or cost limits are exceeded.
/// </summary>
public sealed class UsageMeteringChatClient(IChatClient inner, Func<ModelCallUsage, CancellationToken, Task> onUsage) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        var usage = response.Usage;
        var toolCalls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Count();
        await onUsage(new ModelCallUsage(usage?.InputTokenCount ?? 0, usage?.OutputTokenCount ?? 0, usage?.CachedInputTokenCount ?? 0,
            System.Diagnostics.Stopwatch.GetElapsedTime(started), toolCalls), cancellationToken);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Agents run non-streaming; stream callers still get metered through the buffered path.
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates()) yield return update;
    }
}
