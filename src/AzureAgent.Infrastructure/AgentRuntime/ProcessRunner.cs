using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace AzureAgent.Infrastructure.AgentRuntime;

public sealed record ProcessResult(int ExitCode, string Output, bool TimedOut);

/// <summary>Exact stdout bytes (up to a cap) and stderr text of a process.</summary>
public sealed record CapturedOutput(int ExitCode, byte[] Stdout, bool Truncated, string Stderr);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Like <see cref="RunAsync"/>, but hands every stdout line to <paramref name="onOutputLine"/> as it arrives (stderr is
    /// collected in the result). An exception from the callback kills the process tree and is rethrown.
    /// </summary>
    Task<ProcessResult> RunStreamingAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string>? environment, TimeSpan timeout, Func<string, Task> onOutputLine, CancellationToken ct);

    /// <summary>Captures stdout exactly (binary safe) up to <paramref name="maxBytes"/>; a longer output is cut and the process stopped.</summary>
    Task<CapturedOutput> CaptureAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string>? environment, TimeSpan timeout, int maxBytes, CancellationToken ct);
}

/// <summary>
/// Runs a process without a shell (no argument interpolation), with a timeout and capped output.
/// Children never inherit the server's environment (it holds connection strings and keys): they get
/// only the variables in <see cref="AgentRuntimeOptions.InheritedEnvironment"/> plus the caller's.
/// </summary>
public sealed class ProcessRunner(IOptions<AgentRuntimeOptions> options) : IProcessRunner
{
    private const int MaxOutputChars = 64_000;

    public ProcessStartInfo StartInfo(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string>? environment)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        var inherited = info.Environment.Where(e => options.Value.InheritedEnvironment.Contains(e.Key, StringComparer.Ordinal)).ToList();
        info.Environment.Clear();
        foreach (var (key, value) in inherited) info.Environment[key] = value;
        info.Environment["HOME"] = Path.GetTempPath();
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        if (environment is not null) foreach (var (key, value) in environment) info.Environment[key] = value;
        return info;
    }

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string>? environment, TimeSpan timeout, CancellationToken ct) =>
        RunCoreAsync(StartInfo(fileName, arguments, workingDirectory, environment), timeout, null, ct);

    public Task<ProcessResult> RunStreamingAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string>? environment, TimeSpan timeout, Func<string, Task> onOutputLine, CancellationToken ct) =>
        RunCoreAsync(StartInfo(fileName, arguments, workingDirectory, environment), timeout, onOutputLine, ct);

    public async Task<CapturedOutput> CaptureAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string>? environment, TimeSpan timeout, int maxBytes, CancellationToken ct)
    {
        using var process = new Process { StartInfo = StartInfo(fileName, arguments, workingDirectory, environment) };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) { if (stderr.Length < 16_000) stderr.AppendLine(e.Data); } };
        process.Start();
        process.StandardInput.Close();
        process.BeginErrorReadLine();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        var truncated = false;
        try
        {
            var stdout = process.StandardOutput.BaseStream;
            int read;
            while ((read = await stdout.ReadAsync(chunk, timeoutCts.Token)) > 0)
            {
                var room = maxBytes - (int)buffer.Length;
                buffer.Write(chunk, 0, Math.Min(read, room));
                if (read > room) { truncated = true; Kill(process); break; }
            }
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{fileName} did not finish within {timeout}");
        }
        return new CapturedOutput(truncated ? 0 : process.ExitCode, buffer.ToArray(), truncated, stderr.ToString());
    }

    private static async Task<ProcessResult> RunCoreAsync(ProcessStartInfo info, TimeSpan timeout, Func<string, Task>? onOutputLine, CancellationToken ct)
    {
        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        void Append(string? line)
        {
            if (line is null) return;
            lock (output)
            {
                // Keep the tail: the last lines explain a failure.
                output.AppendLine(line);
                if (output.Length > MaxOutputChars) output.Remove(0, output.Length - MaxOutputChars);
            }
        }
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Start();
        process.StandardInput.Close();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        Exception? callbackError = null;
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
        try
        {
            if (onOutputLine is null)
            {
                process.OutputDataReceived += (_, e) => Append(e.Data);
                process.BeginOutputReadLine();
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            else
            {
                // Read stdout on a background task and wait for the PROCESS to exit, not for stdout to
                // reach EOF. A tool the CLI spawns (e.g. a shell command) can inherit the stdout pipe and
                // keep it open after the CLI itself has exited; waiting for EOF would then block the read —
                // and the whole run — until the timeout. On exit we give the reader a brief grace to flush
                // buffered lines, then stop it and kill the tree to release any lingering child.
                var reader = Task.Run(async () =>
                {
                    try
                    {
                        while (await process.StandardOutput.ReadLineAsync(readCts.Token) is { } line)
                            if (line.Length > 0) await onOutputLine(line);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { callbackError = ex; Kill(process); }
                });
                await process.WaitForExitAsync(timeoutCts.Token);
                readCts.CancelAfter(TimeSpan.FromSeconds(2));
                await reader;
                Kill(process);
                if (callbackError is not null) throw callbackError;
            }
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            ct.ThrowIfCancellationRequested();
            return new ProcessResult(-1, output.ToString(), TimedOut: true);
        }
        catch
        {
            Kill(process);
            throw;
        }
        return new ProcessResult(process.ExitCode, output.ToString(), false);
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }
}
