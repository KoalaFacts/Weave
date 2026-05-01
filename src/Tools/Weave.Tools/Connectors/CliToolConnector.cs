using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Models;

namespace Weave.Tools.Connectors;

public sealed partial class CliToolConnector(ILogger<CliToolConnector> logger) : IToolConnector
{
    private readonly ConcurrentDictionary<string, Weave.Workspaces.Models.CliConfig> _configurations = new();
    private readonly CliCommandPolicy _policy = new();
    private readonly CliProcessRunner _processRunner = new();

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

        var policyResult = _policy.Evaluate(command, cli);
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

        return await _processRunner.RunAsync(handle.ToolName, cli, command, sw, ct);
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
}
