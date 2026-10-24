using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Features.Azure;
using AzureAgent.Infrastructure.AgentRuntime;

namespace AzureAgent.Infrastructure.Messaging;

public static class ExecuteAgentRunHandler
{
    public static async Task Handle(ExecuteAgentRun message, ITenantContext tenant, AgentRunExecutor executor, CancellationToken ct)
    {
        tenant.Use(message.OrganizationId);
        await executor.ExecuteAsync(message.RunId, ct);
    }
}
