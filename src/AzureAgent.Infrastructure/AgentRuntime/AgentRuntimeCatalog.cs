using AzureAgent.Application.Abstractions;
using AzureAgent.Domain.Agents;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace AzureAgent.Infrastructure.AgentRuntime;

using AgentRuntimeKind = AzureAgent.Domain.Agents.AgentRuntime;

/// <summary>Probes the CLI executables with <c>--version</c> (cached for five minutes).</summary>
public sealed class AgentRuntimeCatalog(IProcessRunner processes, ICodingCliFactory clis, IOptions<AgentRuntimeOptions> options, IMemoryCache cache)
    : IAgentRuntimeCatalog
{
    public async Task<IReadOnlyList<AgentRuntimeInfo>> ListAsync(CancellationToken ct) =>
        await cache.GetOrCreateAsync("agent-runtimes", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            var result = new List<AgentRuntimeInfo>
            {
                new(AgentRuntimeKind.Framework, "Microsoft Agent Framework", true, null, "Built in; OpenAI, Azure OpenAI, Anthropic, Gemini"),
            };
            foreach (var runtime in new[] { AgentRuntimeKind.ClaudeCode, AgentRuntimeKind.CursorCli })
                result.Add(await ProbeAsync(clis.Create(runtime), ct));
            return (IReadOnlyList<AgentRuntimeInfo>)result;
        }) ?? [];

    private async Task<AgentRuntimeInfo> ProbeAsync(ICodingCliAdapter cli, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(options.Value.WorkspaceRoot);
            var result = await processes.RunAsync(cli.Executable, ["--version"], options.Value.WorkspaceRoot, null, TimeSpan.FromSeconds(20), ct);
            var version = result.Output.Trim().Split('\n').FirstOrDefault()?.Trim();
            return result.ExitCode == 0
                ? new AgentRuntimeInfo(cli.Runtime, cli.DisplayName, true, version, cli.Executable)
                : new AgentRuntimeInfo(cli.Runtime, cli.DisplayName, false, null, $"'{cli.Executable} --version' exited with {result.ExitCode}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new AgentRuntimeInfo(cli.Runtime, cli.DisplayName, false, null, $"'{cli.Executable}' is not installed on this node");
        }
    }
}
