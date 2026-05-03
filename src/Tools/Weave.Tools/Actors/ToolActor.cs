using Microsoft.Extensions.Logging;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Tools.Discovery;
using Weave.Tools.Events;
using Weave.Tools.Models;

namespace Weave.Tools.Actors;

public sealed partial class ToolActor(
    IVirtualActorProvider actors,
    IToolDiscoveryService discovery,
    ILeakScanner leakScanner,
    ICapabilityTokenService tokenService,
    ILifecycleManager lifecycleManager,
    IEventBus eventBus,
    ILogger<ToolActor> logger) : IToolActor
{
    private readonly ToolActorIdentity _identity = new();
    private readonly ToolInvocationLeakGuard _leakGuard = new(leakScanner, eventBus, logger);
    private readonly ToolSecretSubstitutor _secretSubstitutor = new(actors);
    private ToolHandle? _handle;
    private ToolSpec? _definition;

    public Task OnActivatedAsync(string? key, CancellationToken cancellationToken)
    {
        _identity.Activate(key);
        return Task.CompletedTask;
    }

    public async Task<ToolHandle> ConnectAsync(ToolSpec definition, CapabilityToken token)
    {
        _identity.Ensure(definition, token);
        Authorize(token);

        _definition = definition;

        var context = new LifecycleContext
        {
            WorkspaceId = WorkspaceId.From(_identity.WorkspaceId),
            Phase = LifecyclePhase.ToolConnecting
        };

        await lifecycleManager.RunHooksAsync(LifecyclePhase.ToolConnecting, context, CancellationToken.None);

        var connector = discovery.GetConnector(definition.Type);
        _handle = await connector.ConnectAsync(definition, token);

        await lifecycleManager.RunHooksAsync(
            LifecyclePhase.ToolConnected,
            context with { Phase = LifecyclePhase.ToolConnected },
            CancellationToken.None);

        LogToolConnected(_identity.ToolName, _identity.WorkspaceId);
        return _handle;
    }

    public async Task DisconnectAsync()
    {
        if (_handle is null || _definition is null)
            return;

        var context = new LifecycleContext
        {
            WorkspaceId = WorkspaceId.From(_identity.WorkspaceId),
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
        LogToolDisconnected(_identity.ToolName, _identity.WorkspaceId);
    }

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CapabilityToken token)
    {
        _identity.Ensure(invocation: invocation, token: token);
        Authorize(token);

        if (_handle is null || _definition is null)
            throw new InvalidOperationException($"Tool '{_identity.ToolName}' is not connected");

        var blockedResult = await _leakGuard.BlockIfOutboundLeaksAsync(_identity.WorkspaceId, _identity.ToolName, invocation);
        if (blockedResult is not null)
            return blockedResult;

        var effectiveInvocation = await _secretSubstitutor.SubstituteAsync(_identity.WorkspaceId, invocation);
        var connector = discovery.GetConnector(_definition.Type);
        var result = await connector.InvokeAsync(_handle, effectiveInvocation);
        result = await _leakGuard.RedactIfInboundLeaksAsync(_identity.WorkspaceId, _identity.ToolName, result);

        await eventBus.PublishAsync(new ToolInvocationCompletedEvent
        {
            SourceId = $"{_identity.WorkspaceId}/{_identity.ToolName}",
            ToolName = _identity.ToolName,
            WorkspaceId = WorkspaceId.From(_identity.WorkspaceId),
            Success = result.Success,
            Duration = result.Duration
        }, CancellationToken.None);

        return result;
    }

    public async Task<ToolSchema> GetSchemaAsync()
    {
        if (_handle is null || _definition is null)
            return new ToolSchema { ToolName = _identity.ToolName, Description = "Tool not connected" };

        var connector = discovery.GetConnector(_definition.Type);
        return await connector.DiscoverSchemaAsync(_handle);
    }

    public Task<ToolHandle?> GetHandleAsync() => Task.FromResult(_handle);

    private void Authorize(CapabilityToken token)
    {
        var grant = $"tool:{_identity.ToolName}";

        if (!tokenService.Validate(token))
        {
            logger.LogWarning("Tool capability denied: invalid or expired token for grant '{Grant}' on workspace {WorkspaceId}",
                grant, _identity.WorkspaceId);
            throw new UnauthorizedAccessException("Invalid or expired capability token");
        }

        if (!string.Equals(token.WorkspaceId, _identity.WorkspaceId, StringComparison.Ordinal))
        {
            logger.LogWarning("Tool capability denied: token workspace '{TokenWorkspaceId}' does not match actor workspace '{ActorWorkspaceId}'",
                token.WorkspaceId, _identity.WorkspaceId);
            throw new UnauthorizedAccessException(
                $"Token workspace '{token.WorkspaceId}' does not match actor workspace '{_identity.WorkspaceId}'");
        }

        if (!token.HasGrant(grant))
        {
            logger.LogWarning("Tool capability denied: token issued to '{IssuedTo}' does not grant '{Grant}'",
                token.IssuedTo, grant);
            throw new UnauthorizedAccessException($"Token does not grant access to tool '{_identity.ToolName}'");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool '{Tool}' connected in workspace '{Workspace}'")]
    private partial void LogToolConnected(string tool, string workspace);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool '{Tool}' disconnected from workspace '{Workspace}'")]
    private partial void LogToolDisconnected(string tool, string workspace);
}
