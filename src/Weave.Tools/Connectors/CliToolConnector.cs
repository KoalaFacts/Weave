using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

public sealed class CliToolConnector(ILogger<CliToolConnector> logger) : IToolConnector
{
    private readonly ConcurrentDictionary<string, Weave.Workspaces.Models.CliConfig> _configurations = new();

    public ToolType ToolType => ToolType.Cli;

    public Task<ToolHandle> ConnectAsync(ToolSpec tool, CapabilityToken token, CancellationToken ct = default)
    {
        var cli = tool.Cli ?? throw new InvalidOperationException($"Tool '{tool.Name}' has no CLI configuration");
        var connectionId = $"cli:{tool.Name}:{Guid.NewGuid():N}";
        _configurations[connectionId] = cli;

        logger.LogInformation("CLI tool '{Tool}' connected (shell: {Shell})", tool.Name, cli.Shell);

        return Task.FromResult(new ToolHandle
        {
            ToolName = tool.Name,
            Type = ToolType.Cli,
            ConnectionId = connectionId,
            IsConnected = true
        });
    }

    public Task DisconnectAsync(ToolHandle handle, CancellationToken ct = default)
    {
        _configurations.TryRemove(handle.ConnectionId, out _);
        return Task.CompletedTask;
    }

    public async Task<ToolResult> InvokeAsync(ToolHandle handle, ToolInvocation invocation, CancellationToken ct = default)
    {
        if (!_configurations.TryGetValue(handle.ConnectionId, out var cli))
        {
            return new ToolResult
            {
                Success = false,
                ToolName = handle.ToolName,
                Error = "CLI tool is not connected"
            };
        }

        var command = invocation.RawInput ?? string.Join(" ", invocation.Parameters.Values);
        var sw = Stopwatch.StartNew();

        if (!IsCommandAllowed(command, cli))
        {
            sw.Stop();
            return new ToolResult
            {
                Success = false,
                ToolName = handle.ToolName,
                Error = $"Command '{command}' is not permitted by the CLI tool policy.",
                Duration = sw.Elapsed
            };
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cli.Shell,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            AppendShellArguments(psi, cli.Shell, command);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start CLI process.");
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

    private static bool IsCommandAllowed(string command, Weave.Workspaces.Models.CliConfig cli)
    {
        if (cli.DeniedCommands.Any(pattern => WildcardMatches(pattern, command)))
            return false;

        if (cli.AllowedCommands.Count == 0)
            return true;

        return cli.AllowedCommands.Any(pattern => WildcardMatches(pattern, command));
    }

    private static void AppendShellArguments(ProcessStartInfo psi, string shell, string command)
    {
        if (shell.EndsWith("powershell", StringComparison.OrdinalIgnoreCase) ||
            shell.EndsWith("pwsh", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);
            return;
        }

        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
    }

    private static bool WildcardMatches(string pattern, string command)
    {
        if (pattern == "*")
            return true;

        var parts = pattern.Split('*', StringSplitOptions.None);
        var currentIndex = 0;
        var anchoredAtStart = !pattern.StartsWith('*');
        var anchoredAtEnd = !pattern.EndsWith('*');

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0)
                continue;

            var matchIndex = command.IndexOf(part, currentIndex, StringComparison.Ordinal);
            if (matchIndex < 0)
                return false;

            if (i == 0 && anchoredAtStart && matchIndex != 0)
                return false;

            currentIndex = matchIndex + part.Length;
        }

        if (anchoredAtEnd)
        {
            var lastPart = parts.LastOrDefault(static p => p.Length > 0) ?? string.Empty;
            return command.EndsWith(lastPart, StringComparison.Ordinal);
        }

        return true;
    }
}
