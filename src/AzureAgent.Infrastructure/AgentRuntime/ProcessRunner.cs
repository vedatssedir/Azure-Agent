using System.Diagnostics;
using System.Text;

namespace AzureAgent.Infrastructure.AgentRuntime;

public sealed record ProcessResult(int ExitCode, string Output, bool TimedOut);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout, CancellationToken ct);
}

/// <summary>Runs a process without a shell (no argument interpolation), with a timeout and capped output.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    private const int MaxOutputChars = 64_000;

    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string>? environment, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (environment is not null) foreach (var (key, value) in environment) info.Environment[key] = value;

        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        void Append(string? line)
        {
            if (line is null) return;
            lock (output) { if (output.Length < MaxOutputChars) output.AppendLine(line); }
        }
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            return new ProcessResult(-1, output.ToString(), TimedOut: true);
        }
        return new ProcessResult(process.ExitCode, output.ToString(), false);
    }
}
