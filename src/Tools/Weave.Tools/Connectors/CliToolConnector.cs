using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Models;
using Weave.Workspaces.Models;

namespace Weave.Tools.Connectors;

public sealed partial class CliToolConnector(ILogger<CliToolConnector> logger) : IToolConnector
{
    private static readonly string[] ShellMetacharacters = [";", "|", "&&", "||", "`", "$(", "$((", "\n", "\r", ">>", ">&"];

    private readonly ConcurrentDictionary<string, CliConfig> _configurations = new();

    public ToolType ToolType => ToolType.Cli;

    public Task<ToolHandle> ConnectAsync(ToolSpec tool, CapabilityToken token, CancellationToken ct = default)
    {
        var cli = tool.Cli ?? throw new InvalidOperationException($"Tool '{tool.Name}' has no CLI configuration");
        var connectionId = $"cli:{tool.Name}:{Guid.NewGuid():N}";
        _configurations[connectionId] = cli;

        LogCliToolConnected(tool.Name, cli.Shell);

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

        var policyResult = EvaluateCommandPolicy(command, cli);
        if (!policyResult.IsAllowed)
        {
            sw.Stop();
            return new ToolResult
            {
                Success = false,
                ToolName = handle.ToolName,
                Error = policyResult.Error,
                Duration = sw.Elapsed
            };
        }

        return await RunProcessAsync(logger, handle.ToolName, cli, command, sw, ct);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "CLI tool '{Tool}' connected (shell: {Shell})")]
    private partial void LogCliToolConnected(string tool, string shell);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CLI tool invocation failed for '{Tool}'")]
    private static partial void LogCliToolInvocationFailed(ILogger logger, Exception ex, string tool);

    private static CliCommandPolicyResult EvaluateCommandPolicy(string command, CliConfig config)
    {
        if (ContainsShellMetacharacters(command))
            return CliCommandPolicyResult.Blocked("Command contains prohibited shell metacharacters.");

        if (!IsCommandAllowed(command, config))
            return CliCommandPolicyResult.Blocked($"Command '{command}' is not permitted by the CLI tool policy.");

        return CliCommandPolicyResult.Allowed;
    }

    private static bool ContainsShellMetacharacters(string command) =>
        ShellMetacharacters.Any(meta => command.Contains(meta, StringComparison.Ordinal));

    private static bool IsCommandAllowed(string command, CliConfig config)
    {
        if (config.DeniedCommands.Any(pattern => WildcardMatches(pattern, command)))
            return false;

        if (config.AllowedCommands.Count == 0)
            return true;

        return config.AllowedCommands.Any(pattern => WildcardMatches(pattern, command));
    }

    private static bool WildcardMatches(string pattern, string command)
    {
        if (pattern == "*")
            return true;

        var parts = pattern.Split('*', StringSplitOptions.None);
        var currentIndex = 0;
        var anchoredAtStart = !pattern.StartsWith('*');
        var anchoredAtEnd = !pattern.EndsWith('*');

        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length == 0)
                continue;

            var matchIndex = command.IndexOf(part, currentIndex, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
                return false;

            if (index == 0 && anchoredAtStart && matchIndex != 0)
                return false;

            currentIndex = matchIndex + part.Length;
        }

        if (!anchoredAtEnd)
            return true;

        var lastPart = parts.LastOrDefault(static p => p.Length > 0) ?? string.Empty;
        return command.EndsWith(lastPart, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ToolResult> RunProcessAsync(
        ILogger logger,
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
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or ObjectDisposedException)
        {
            sw.Stop();
            LogCliToolInvocationFailed(logger, ex, toolName);
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

    private readonly record struct CliCommandPolicyResult(bool IsAllowed, string? Error)
    {
        public static CliCommandPolicyResult Allowed { get; } = new(true, null);

        public static CliCommandPolicyResult Blocked(string error) => new(false, error);
    }
}
