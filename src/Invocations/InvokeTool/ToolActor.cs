using Microsoft.Extensions.Logging;
using Weave.Authority;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Tools.Discovery;
using Weave.Tools.Marketplace;
namespace Weave.Tools.Tool;

public sealed partial class ToolActor(
    IVirtualActorProvider actors,
    IToolDiscoveryService discovery,
    ILeakScanner leakScanner,
    ICapabilityAuthorizer authorizer,
    ILifecycleManager lifecycleManager,
    IEventBus eventBus,
    ILogger<ToolActor> logger) : IToolActor
{
    private readonly ToolActorIdentity _identity = new();
    private readonly ToolInvocationLeakGuard _leakGuard = new(leakScanner, eventBus, logger);
    private readonly ToolSecretSubstitutor _secretSubstitutor = new(actors);
    private ToolActorConnection? _connection;
    private long _generation;

    public Task OnActivatedAsync(string? key, CancellationToken cancellationToken)
    {
        _identity.Activate(key);
        return Task.CompletedTask;
    }

    public async Task<ToolHandle> ConnectAsync(ToolSpec definition, CapabilityToken token)
    {
        _identity.Ensure(definition, token);
        token = CaptureToken(token);
        var grant = ToolCapability.Connect(_identity.ToolName);
        await authorizer.AuthorizeAsync(token, grant, _identity.WorkspaceId);
        token.CancellationToken.ThrowIfCancellationRequested();
        var generation = Interlocked.Increment(ref _generation);
        var context = new LifecycleContext
        {
            WorkspaceId = WorkspaceId.From(_identity.WorkspaceId),
            Phase = LifecyclePhase.ToolConnecting
        };
        await lifecycleManager.RunHooksAsync(LifecyclePhase.ToolConnecting, context, token.CancellationToken);
        await authorizer.AuthorizeAsync(token, grant, _identity.WorkspaceId);
        token.CancellationToken.ThrowIfCancellationRequested();
        var connector = discovery.GetConnector(definition.Type);
        var handle = await connector.ConnectAsync(definition, token, token.CancellationToken);
        if (generation != Interlocked.Read(ref _generation))
        {
            await connector.DisconnectAsync(handle);
            throw new InvalidOperationException("Tool connection changed while connecting.");
        }
        var previous = Interlocked.Exchange(ref _connection, new ToolActorConnection(connector, handle));
        if (previous is not null && previous.Handle.ConnectionId != handle.ConnectionId)
            await previous.Connector.DisconnectAsync(previous.Handle);
        await lifecycleManager.RunHooksAsync(
            LifecyclePhase.ToolConnected,
            context with { Phase = LifecyclePhase.ToolConnected },
            token.CancellationToken);
        LogToolConnected(_identity.ToolName, _identity.WorkspaceId);
        return handle;
    }

    public async Task DisconnectAsync()
    {
        Interlocked.Increment(ref _generation);
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is null)
            return;
        var context = new LifecycleContext
        {
            WorkspaceId = WorkspaceId.From(_identity.WorkspaceId),
            Phase = LifecyclePhase.ToolDisconnecting
        };
        await lifecycleManager.RunHooksAsync(LifecyclePhase.ToolDisconnecting, context, CancellationToken.None);
        await connection.Connector.DisconnectAsync(connection.Handle);
        await lifecycleManager.RunHooksAsync(
            LifecyclePhase.ToolDisconnected,
            context with { Phase = LifecyclePhase.ToolDisconnected },
            CancellationToken.None);
        LogToolDisconnected(_identity.ToolName, _identity.WorkspaceId);
    }

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CapabilityToken token)
    {
        _identity.Ensure(invocation: invocation, token: token);
        token = CaptureToken(token);
        var connection = _connection
            ?? throw new InvalidOperationException($"Tool '{_identity.ToolName}' is not connected");
        var generation = Interlocked.Read(ref _generation);
        var ownedInput = invocation with { Parameters = new(invocation.Parameters, StringComparer.Ordinal) };
        var normalized = connection.Connector.NormalizeInvocation(ownedInput);
        _identity.Ensure(invocation: normalized, token: token);
        normalized = normalized with { Parameters = new(normalized.Parameters, StringComparer.Ordinal) };
        var grant = ToolCapability.Invoke(_identity.ToolName, normalized.Method);
        await authorizer.AuthorizeAsync(token, grant, _identity.WorkspaceId);
        token.CancellationToken.ThrowIfCancellationRequested();
        var blockedResult = await _leakGuard.BlockIfOutboundLeaksAsync(_identity.WorkspaceId, _identity.ToolName, normalized);
        if (blockedResult is not null)
            return blockedResult;
        var effectiveInvocation = await _secretSubstitutor.SubstituteAsync(_identity.WorkspaceId, normalized);
        await authorizer.AuthorizeAsync(token, grant, _identity.WorkspaceId);
        token.CancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(connection, _connection) || generation != Interlocked.Read(ref _generation))
            throw new InvalidOperationException("Tool connection changed before dispatch; request a new invocation.");
        var result = await connection.Connector.InvokeAsync(connection.Handle, effectiveInvocation, token.CancellationToken);
        result = await _leakGuard.RedactIfInboundLeaksAsync(_identity.WorkspaceId, _identity.ToolName, result);
        await eventBus.PublishAsync(new ToolInvocationCompletedEvent
        {
            SourceId = $"{_identity.WorkspaceId}/{_identity.ToolName}",
            ToolName = _identity.ToolName,
            WorkspaceId = WorkspaceId.From(_identity.WorkspaceId),
            Success = result.Success,
            Duration = result.Duration
        }, token.CancellationToken);
        return result;
    }

    public async Task<ToolSchema> GetSchemaAsync()
    {
        var connection = _connection;
        return connection is null
            ? new ToolSchema { ToolName = _identity.ToolName, Description = "Tool not connected" }
            : await connection.Connector.DiscoverSchemaAsync(connection.Handle);
    }

    public Task<ToolHandle?> GetHandleAsync() => Task.FromResult(_connection?.Handle);

    private static CapabilityToken CaptureToken(CapabilityToken token)
    {
        if (token.Grants is null)
            throw new UnauthorizedAccessException("Capability token has no grant collection.");
        return token with { Grants = new(token.Grants, StringComparer.Ordinal) };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool '{Tool}' connected in workspace '{Workspace}'")]
    private partial void LogToolConnected(string tool, string workspace);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool '{Tool}' disconnected from workspace '{Workspace}'")]
    private partial void LogToolDisconnected(string tool, string workspace);
}
