using AzureAgent.Domain.Agents;
using AzureAgent.Infrastructure.AgentRuntime;
using Microsoft.Extensions.AI;

namespace AzureAgent.IntegrationTests.Fakes;

/// <summary>
/// Model stand-in: each call pops the next scripted response. Developer runs write a file through
/// the workspace tools; reviewer runs return review JSON. Usage is reported like a real provider.
/// </summary>
public sealed class ScriptedChatClients : IChatClientFactory
{
    public Queue<Func<IReadOnlyList<ChatMessage>, ChatResponse>> Script { get; } = new();
    public List<(ModelProvider Provider, string Model, string ApiKey)> Created { get; } = [];
    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
    public long InputTokensPerCall { get; set; } = 10_000;
    public long OutputTokensPerCall { get; set; } = 1_000;

    public IChatClient Create(ModelProvider provider, string model, string apiKey, string? endpoint)
    {
        lock (Created) Created.Add((provider, model, apiKey));
        return new Client(this);
    }

    public static ChatResponse ToolCall(string name, Dictionary<string, object?> args) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, args)]));

    public static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    private sealed class Client(ScriptedChatClients owner) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            Func<IReadOnlyList<ChatMessage>, ChatResponse> next;
            lock (owner.Script)
            {
                owner.Requests.Add(list);
                next = owner.Script.Count > 0 ? owner.Script.Dequeue() : _ => Text("Done.");
            }
            var response = next(list);
            response.Usage = new UsageDetails { InputTokenCount = owner.InputTokensPerCall, OutputTokenCount = owner.OutputTokensPerCall, CachedInputTokenCount = 0 };
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }
}
