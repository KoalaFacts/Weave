using System.Diagnostics;

namespace Weave.Workspaces.Runtime;

public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<string> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process '{command}'.");

        // Start both reads CONCURRENTLY. If the child writes a lot to stderr
        // while stdout is idle, sequential reads would deadlock — we'd be
        // awaiting stdout while the kernel blocks the child on a full
        // stderr pipe.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Command '{command}' failed (exit code {process.ExitCode}): {stderr}");

        return stdout;
    }
}
