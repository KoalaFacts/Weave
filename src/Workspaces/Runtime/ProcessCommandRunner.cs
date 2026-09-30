using System.Diagnostics;
using System.Text;

namespace Weave.Workspaces.Runtime;

public sealed class ProcessCommandRunner : ICommandRunner
{
    private const int OutputLimit = 65_536;

    public async Task<string> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
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

        var stdoutTask = DrainAsync(process.StandardOutput, ct);
        var stderrTask = DrainAsync(process.StandardError, ct);
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(ct);
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    // The process exited between checking and killing it.
                }
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (stdout.Exceeded || stderr.Exceeded)
            throw new InvalidOperationException("Command output limit exceeded.");

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Command '{command}' failed (exit code {process.ExitCode}).");

        return stdout.Text;
    }

    private static async Task<(string Text, bool Exceeded)> DrainAsync(StreamReader reader, CancellationToken ct)
    {
        var retained = new StringBuilder();
        var buffer = new char[4096];
        var exceeded = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            var keep = Math.Min(count, OutputLimit - retained.Length);
            retained.Append(buffer, 0, keep);
            exceeded |= keep < count;
        }
        return (retained.ToString(), exceeded);
    }
}
