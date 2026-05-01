using System.Diagnostics;
using Weave.Tools.Models;
using Weave.Workspaces.Models;

namespace Weave.Tools.Connectors;

internal sealed class CliProcessRunner
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from CliToolConnector.")]
    public async Task<ToolResult> RunAsync(
        string toolName,
        CliConfig config,
        string command,
        Stopwatch sw,
        CancellationToken ct)
    {
        try
        {
            var processStart = new ProcessStartInfo
            {
                FileName = config.Shell,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            AppendShellArguments(processStart, config.Shell, command);

            using var process = Process.Start(processStart) ?? throw new InvalidOperationException("Failed to start CLI process.");
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);

            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync(ct);

            var output = await outputTask;
            var error = await errorTask;
            sw.Stop();

            return new ToolResult
            {
                Success = process.ExitCode == 0,
                ToolName = toolName,
                Output = output,
                Error = string.IsNullOrEmpty(error) ? null : error,
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ToolResult
            {
                Success = false,
                ToolName = toolName,
                Error = ex.Message,
                Duration = sw.Elapsed
            };
        }
    }

    private static void AppendShellArguments(ProcessStartInfo processStart, string shell, string command)
    {
        if (shell.EndsWith("powershell", StringComparison.OrdinalIgnoreCase) ||
            shell.EndsWith("pwsh", StringComparison.OrdinalIgnoreCase))
        {
            processStart.ArgumentList.Add("-Command");
            processStart.ArgumentList.Add(command);
            return;
        }

        processStart.ArgumentList.Add("-c");
        processStart.ArgumentList.Add(command);
    }
}
