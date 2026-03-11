using Microsoft.Extensions.Logging;
using Orleans;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Shared.Lifecycle;
using Weave.Tools.Discovery;
using Weave.Tools.Models;

namespace Weave.Tools.Grains;

public sealed class ToolGrain(
    IToolDiscoveryService discovery,
    ILeakScanner leakScanner,
    ICapabilityTokenService tokenService,
    ILifecycleManager lifecycleManager,
    ILogger<ToolGrain> logger) : Grain, IToolGrain
{
    private ToolHandle? _handle;
    private ToolSpec? _definition;
    private string _workspaceId = string.Empty;
    private string _toolName = string.Empty;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var key = this.GetPrimaryKeyString();
        var parts = key.Split('/', 2);
        _workspaceId = parts.Length > 1 ? parts[0] : key;
        _toolName = parts.Length > 1 ? parts[1] : key;
        return Task.CompletedTask;
    }

    public async Task<ToolHandle> ConnectAsync(ToolSpec definition, CapabilityToken token)
    {
        if (!tokenService.Validate(token))
            throw new UnauthorizedAccessException("Invalid or expired capability token");

        if (!token.HasGrant($"tool:{_toolName}") && !token.HasGrant("tool:*"))
            throw new UnauthorizedAccessException($"Token does not grant access to tool '{_toolName}'");

        _definition = definition;

        var context = new LifecycleContext
        {
            WorkspaceId = Shared.Ids.WorkspaceId.From(_workspaceId),
            Phase = LifecyclePhase.ToolConnecting
        };

        await lifecycleManager.RunHooksAsync(LifecyclePhase.ToolConnecting, context, CancellationToken.None);

        var connector = discovery.GetConnector(definition.Type);
        _handle = await connector.ConnectAsync(definition, token);

        await lifecycleManager.RunHooksAsync(LifecyclePhase.ToolConnected,
            context with { Phase = LifecyclePhase.ToolConnected }, CancellationToken.None);

        logger.LogInformation("Tool '{Tool}' connected in workspace '{Workspace}'", _toolName, _workspaceId);
        return _handle;
    }

    public async Task DisconnectAsync()
    {
        if (_handle is null || _definition is null)
            return;

        var context = new LifecycleContext
        {
            WorkspaceId = Shared.Ids.WorkspaceId.From(_workspaceId),
            Phase = LifecyclePhase.ToolDisconnecting
        };

        await lifecycleManager.RunHooksAsync(LifecyclePhase.ToolDisconnecting, context, CancellationToken.None);

        var connector = discovery.GetConnector(_definition.Type);
        await connector.DisconnectAsync(_handle);

        await lifecycleManager.RunHooksAsync(LifecyclePhase.ToolDisconnected,
            context with { Phase = LifecyclePhase.ToolDisconnected }, CancellationToken.None);

        _handle = null;
        logger.LogInformation("Tool '{Tool}' disconnected from workspace '{Workspace}'", _toolName, _workspaceId);
    }

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CapabilityToken token)
    {
        if (!tokenService.Validate(token))
            throw new UnauthorizedAccessException("Invalid or expired capability token");

        if (_handle is null || _definition is null)
            throw new InvalidOperationException($"Tool '{_toolName}' is not connected");

        // Scan outbound payload for secret leaks
        if (invocation.RawInput is not null)
        {
            var scanContext = new ScanContext
            {
                WorkspaceId = _workspaceId,
                SourceComponent = $"tool:{_toolName}",
                Direction = ScanDirection.Outbound
            };
            var scanResult = await leakScanner.ScanStringAsync(invocation.RawInput, scanContext);
            if (scanResult.HasLeaks)
            {
                logger.LogWarning("Secret leak detected in tool invocation for '{Tool}' — blocked", _toolName);
                return new ToolResult
                {
                    Success = false,
                    ToolName = _toolName,
                    Error = "Tool invocation blocked: potential secret leak detected in payload"
                };
            }
        }

        var connector = discovery.GetConnector(_definition.Type);
        var result = await connector.InvokeAsync(_handle, invocation);

        // Scan response for secret leaks
        if (result.Success && !string.IsNullOrEmpty(result.Output))
        {
            var responseScanContext = new ScanContext
            {
                WorkspaceId = _workspaceId,
                SourceComponent = $"tool:{_toolName}",
                Direction = ScanDirection.Inbound
            };
            var responseScan = await leakScanner.ScanStringAsync(result.Output, responseScanContext);
            if (responseScan.HasLeaks)
            {
                logger.LogWarning("Secret leak detected in tool response from '{Tool}' — redacted", _toolName);
                return result with { Output = "***REDACTED: potential secret detected in response***" };
            }
        }

        return result;
    }

    public async Task<ToolSchema> GetSchemaAsync()
    {
        if (_handle is null || _definition is null)
            return new ToolSchema { ToolName = _toolName, Description = "Tool not connected" };

        var connector = discovery.GetConnector(_definition.Type);
        return await connector.DiscoverSchemaAsync(_handle);
    }

    public Task<ToolHandle?> GetHandleAsync() => Task.FromResult(_handle);
}
