using AzureAgent.Application.Abstractions;
using AzureAgent.Application.Features.Secrets;
using AzureAgent.Domain.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace AzureAgent.Infrastructure.AgentRuntime;

/// <summary>MCP clients opened for one run; disposing closes them (and stops stdio servers).</summary>
public sealed class McpSession(IReadOnlyList<McpClient> clients, IReadOnlyList<AITool> tools) : IAsyncDisposable
{
    public IReadOnlyList<AITool> Tools { get; } = tools;

    public async ValueTask DisposeAsync()
    {
        foreach (var client in clients) await client.DisposeAsync();
    }
}

public sealed class McpToolLoader(IAppDbContext db, SecretResolver secrets, ILoggerFactory loggers)
{
    /// <summary>The agent's MCP servers with resolved secrets, for CLI agents that start the servers themselves.</summary>
    public async Task<IReadOnlyList<CliMcpServer>> DescribeAsync(Guid projectId, Agent agent, CancellationToken ct)
    {
        var ids = agent.McpServers.Select(m => m.McpServerId).ToList();
        if (ids.Count == 0) return [];
        var result = new List<CliMcpServer>();
        foreach (var server in await db.McpServers.AsNoTracking().Where(s => ids.Contains(s.Id)).OrderBy(s => s.Name).ToListAsync(ct))
        {
            var values = new Dictionary<string, string>();
            foreach (var name in server.SecretNames)
                if (await secrets.GetAsync(projectId, name, ct) is { } value) values[name] = value;
            result.Add(new CliMcpServer(server.Name, server.Transport, server.Command, server.Arguments, server.Url, values));
        }
        return result;
    }

    public async Task<McpSession> LoadAsync(Guid projectId, Agent agent, CancellationToken ct)
    {
        var ids = agent.McpServers.Select(m => m.McpServerId).ToList();
        if (ids.Count == 0) return new McpSession([], []);
        var servers = await db.McpServers.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync(ct);
        var clients = new List<McpClient>();
        var tools = new List<AITool>();
        foreach (var server in servers)
        {
            var env = new Dictionary<string, string?>();
            foreach (var name in server.SecretNames) env[name] = await secrets.GetAsync(projectId, name, ct);
            IClientTransport transport = server.Transport == McpTransport.Stdio
                ? new StdioClientTransport(new StdioClientTransportOptions { Name = server.Name, Command = server.Command!, Arguments = server.Arguments, EnvironmentVariables = env }, loggers)
                : new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(server.Url!),
                    Name = server.Name,
                    AdditionalHeaders = env.Where(e => e.Value is not null).ToDictionary(e => e.Key, e => e.Value!),
                }, loggers);
            var client = await McpClient.CreateAsync(transport, loggerFactory: loggers, cancellationToken: ct);
            clients.Add(client);
            tools.AddRange(await client.ListToolsAsync(cancellationToken: ct));
        }
        return new McpSession(clients, tools);
    }
}
