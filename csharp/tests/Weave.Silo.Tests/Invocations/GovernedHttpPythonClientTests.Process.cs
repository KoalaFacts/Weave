using System.Diagnostics;
using System.Text;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class GovernedHttpEntryTests
{
    private const int PythonOutputLimit = 1_048_576;

    private static async Task<(string Output, string Error, int ExitCode, bool[] ReaderWasThreadPool)> RunPythonAsync(
        string script, string input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("python3")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        using var process = new Process { StartInfo = start };
        process.Start().ShouldBeTrue();
        var stdout = DrainPythonPipeAsync(process.StandardOutput);
        var stderr = DrainPythonPipeAsync(process.StandardError);
        var readers = Task.WhenAll(stdout, stderr);
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), ct);
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
            var output = await readers.WaitAsync(ct);
            return (output[0].Text, output[1].Text, process.ExitCode, [output[0].OnThreadPool, output[1].OnThreadPool]);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cleanup.Token);
            }
            // Observe both readers before disposing the process and its pipe handles.
            await readers.WaitAsync(cleanup.Token);
        }
    }

    private static Task<(string Text, bool OnThreadPool)> DrainPythonPipeAsync(StreamReader reader) => Task.Factory.StartNew(() =>
    {
        // Windows process pipes are synchronous; blocking reads must not consume host worker threads.
        var onThreadPool = Thread.CurrentThread.IsThreadPoolThread;
        var output = new StringBuilder();
        var buffer = new char[4096];
        var exceeded = false;
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) != 0)
        {
            var retained = Math.Min(count, PythonOutputLimit - output.Length);
            output.Append(buffer, 0, retained);
            exceeded |= retained != count;
        }
        if (exceeded)
            throw new InvalidOperationException("Python client output exceeded the test capture limit.");
        return (output.ToString(), onThreadPool);
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
