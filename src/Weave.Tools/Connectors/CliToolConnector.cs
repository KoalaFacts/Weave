using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

public sealed class CliToolConnector(ILogger<CliToolConnector> logger) : IToolConnector
{
    public ToolType ToolType => ToolType.Cli;

    public Task<ToolHandle> ConnectAsync(ToolSpec tool, CapabilityToken token, CancellationToken ct = default)
    {
        var cli = tool.Cli ?? throw new InvalidOperationException($"Tool '{tool.Name}' has no CLI configuration");

        logger.LogInformation("CLI tool '{Tool}' connected (shell: {Shell})", tool.Name, cli.Shell);

        return Task.FromResult(new ToolHandle
        {
            ToolName = tool.Name,
            Type = ToolType.Cli,
            ConnectionId = $"cli:{tool.Name}",
            IsConnected = true
        });
    }

    public Task DisconnectAsync(ToolHandle handle, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public async Task<ToolResult> InvokeAsync(ToolHandle handle, ToolInvocation invocation, CancellationToken ct = default)
    {
        var command = invocation.RawInput ?? string.Join(" ", invocation.Parameters.Values);
        var sw = Stopwatch.StartNew();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)!;
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            var error = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            sw.Stop();

            return new ToolResult
            {
                Success = process.ExitCode == 0,
                ToolName = handle.ToolName,
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
                ToolName = handle.ToolName,
                Error = ex.Message,
                Duration = sw.Elapsed
            };
        }
    }

    public Task<ToolSchema> DiscoverSchemaAsync(ToolHandle handle, CancellationToken ct = default)
    {
        return Task.FromResult(new ToolSchema
        {
            ToolName = handle.ToolName,
            Description = $"CLI tool: {handle.ToolName}",
            Parameters =
            [
                new ToolParameter
                {
                    Name = "command",
                    Type = "string",
                    Description = "Shell command to execute",
                    Required = true
                }
            ]
        });
    }
}
