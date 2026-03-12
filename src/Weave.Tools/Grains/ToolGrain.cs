using System.Text.Json;
using Microsoft.Extensions.Logging;
using Weave.Security.Grains;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Tools.Discovery;
using Weave.Tools.Events;
using Weave.Tools.Models;

namespace Weave.Tools.Grains;

public sealed class ToolGrain(
    IGrainFactory grainFactory,
    IToolDiscoveryService discovery,
    ILeakScanner leakScanner,
    ICapabilityTokenService tokenService,
    ILifecycleManager lifecycleManager,
    IEventBus eventBus,
    ILogger<ToolGrain> logger) : Grain, IToolGrain
{
    private ToolHandle? _handle;
    private ToolSpec? _definition;
    private string _workspaceId = string.Empty;
    private string _toolName = string.Empty;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        EnsureIdentity();
        return Task.CompletedTask;
    }

    public async Task<ToolHandle> ConnectAsync(ToolSpec definition, CapabilityToken token)
    {
        EnsureIdentity(definition, token);
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

        await lifecycleManager.RunHooksAsync(
            LifecyclePhase.ToolConnected,
            context with { Phase = LifecyclePhase.ToolConnected },
            CancellationToken.None);

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

        await lifecycleManager.RunHooksAsync(
            LifecyclePhase.ToolDisconnected,
            context with { Phase = LifecyclePhase.ToolDisconnected },
            CancellationToken.None);

        _handle = null;
        logger.LogInformation("Tool '{Tool}' disconnected from workspace '{Workspace}'", _toolName, _workspaceId);
    }

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CapabilityToken token)
    {
        EnsureIdentity(invocation: invocation, token: token);
        if (!tokenService.Validate(token))
            throw new UnauthorizedAccessException("Invalid or expired capability token");

        if (_handle is null || _definition is null)
            throw new InvalidOperationException($"Tool '{_toolName}' is not connected");

        var wsId = Shared.Ids.WorkspaceId.From(_workspaceId);

        // Scan the original outbound request before placeholders are substituted at the network boundary.
        var outboundPayload = invocation.RawInput ?? JsonSerializer.Serialize(invocation.Parameters);
        if (!string.IsNullOrWhiteSpace(outboundPayload))
        {
            var scanContext = new ScanContext
            {
                WorkspaceId = _workspaceId,
                SourceComponent = $"tool:{_toolName}",
                Direction = ScanDirection.Outbound
            };
            var scanResult = await leakScanner.ScanStringAsync(outboundPayload, scanContext);
            if (scanResult.HasLeaks)
            {
                logger.LogWarning("Secret leak detected in tool invocation for '{Tool}' - blocked", _toolName);

                await eventBus.PublishAsync(new ToolInvocationBlockedEvent
                {
                    SourceId = $"{_workspaceId}/{_toolName}",
                    ToolName = _toolName,
                    WorkspaceId = wsId,
                    Reason = "Secret leak detected in outbound payload"
                }, CancellationToken.None);

                return new ToolResult
                {
                    Success = false,
                    ToolName = _toolName,
                    Error = "Tool invocation blocked: potential secret leak detected in payload"
                };
            }
        }

        var effectiveInvocation = await SubstituteSecretsAsync(invocation);
        var connector = discovery.GetConnector(_definition.Type);
        var result = await connector.InvokeAsync(_handle, effectiveInvocation);

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
                logger.LogWarning("Secret leak detected in tool response from '{Tool}' - redacted", _toolName);

                await eventBus.PublishAsync(new ToolInvocationBlockedEvent
                {
                    SourceId = $"{_workspaceId}/{_toolName}",
                    ToolName = _toolName,
                    WorkspaceId = wsId,
                    Reason = "Secret leak detected in inbound response"
                }, CancellationToken.None);

                return result with { Output = "***REDACTED: potential secret detected in response***" };
            }
        }

        await eventBus.PublishAsync(new ToolInvocationCompletedEvent
        {
            SourceId = $"{_workspaceId}/{_toolName}",
            ToolName = _toolName,
            WorkspaceId = wsId,
            Success = result.Success,
            Duration = result.Duration
        }, CancellationToken.None);

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

    private async Task<ToolInvocation> SubstituteSecretsAsync(ToolInvocation invocation)
    {
        var proxy = grainFactory.GetGrain<ISecretProxyGrain>(_workspaceId);
        var parameters = new Dictionary<string, string>(invocation.Parameters.Count, StringComparer.Ordinal);
        foreach (var (key, value) in invocation.Parameters)
            parameters[key] = await proxy.SubstituteAsync(value);

        return invocation with
        {
            Parameters = parameters,
            RawInput = invocation.RawInput is null ? null : await proxy.SubstituteAsync(invocation.RawInput)
        };
    }

    private void EnsureIdentity(ToolSpec? definition = null, CapabilityToken? token = null, ToolInvocation? invocation = null)
    {
        if (string.IsNullOrWhiteSpace(_workspaceId) || string.IsNullOrWhiteSpace(_toolName))
        {
            try
            {
                var key = this.GetPrimaryKeyString();
                var parts = key.Split('/', 2);
                _workspaceId = parts.Length > 1 ? parts[0] : key;
                _toolName = parts.Length > 1 ? parts[1] : key;
            }
            catch (NullReferenceException)
            {
            }
        }

        if (string.IsNullOrWhiteSpace(_workspaceId))
            _workspaceId = token?.WorkspaceId ?? "unknown-workspace";

        if (string.IsNullOrWhiteSpace(_toolName))
            _toolName = definition?.Name ?? invocation?.ToolName ?? "tool";
    }
}
