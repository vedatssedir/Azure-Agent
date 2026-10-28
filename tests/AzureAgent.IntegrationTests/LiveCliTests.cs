using AzureAgent.Domain.Agents;
using AzureAgent.Infrastructure.AgentRuntime;
using Microsoft.Extensions.Options;
using Shouldly;

namespace AzureAgent.IntegrationTests;

/// <summary>
/// Smoke test against the real Claude Code executable, without credentials (private HOME, no key):
/// the CLI must accept the platform's exact command line and the parser must read its real output.
/// Opt-in: AZURE_AGENT_CLI_SMOKE=1 (and `claude` on PATH or AgentRuntime__ClaudeCliPath).
/// </summary>
public sealed class LiveCliTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cli-smoke-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task Real_claude_cli_accepts_the_command_line_and_reports_the_missing_login()
    {
        if (Environment.GetEnvironmentVariable("AZURE_AGENT_CLI_SMOKE") != "1") Assert.Skip("AZURE_AGENT_CLI_SMOKE is not set");
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        var home = Directory.CreateDirectory(Path.Combine(_root, "home")).FullName;
        var cli = new ClaudeCodeAdapter(Environment.GetEnvironmentVariable("AgentRuntime__ClaudeCliPath") ?? "claude");
        var (args, env) = cli.Prepare(new CliInvocation("claude-sonnet-4-5", work, home, "Be brief.", "Say hello", ["dotnet"], false, 2, 0.5m,
            new Dictionary<string, string>()));
        var events = new List<CliEvent>();
        var runner = new ProcessRunner(Options.Create(new AgentRuntimeOptions()));
        var exit = await runner.RunStreamingAsync(cli.Executable, args, work, env, TimeSpan.FromMinutes(2),
            line => { events.AddRange(cli.Parse(line)); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        exit.Output.ShouldNotContain("unknown option");
        var result = events.OfType<CliResult>().ShouldHaveSingleItem();
        result.IsError.ShouldBeTrue();
        result.Text.ShouldContain("login", Case.Insensitive);
    }
}
