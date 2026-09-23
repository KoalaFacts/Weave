using Microsoft.Extensions.Logging;
using Weave.Invocations;
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
    ILogger<ToolActor> logger,
    IInvocationJournal journal,
    TimeProvider timeProvider) : IToolActor
{
    private readonly ToolActorIdentity _identity = new();
    private readonly ToolInvocationLeakGuard _leakGuard = new(leakScanner, eventBus, logger);
    private readonly ToolSecretSubstitutor _secretSubstitutor = new(actors);
    private readonly InvocationExecution _execution = new(journal, timeProvider, logger);
    private ToolHandle? _handle;
    private ToolSpec? _definition;
    private long _connectionVersion;

    public Task OnActivatedAsync(string? key, CancellationToken cancellationToken)
    {
        _identity.Activate(key);
        return Task.CompletedTask;
    }

    public async Task<ToolHandle> ConnectAsync(ToolSpec definition, CapabilityToken token)
    {
        _identity.Ensure(definition, token);
        token = token with { Grants = new HashSet<string>(token.Grants, StringComparer.Ordinal) };
        token.CancellationToken.ThrowIfCancellationRequested();
        await authorizer.AuthorizeAsync(token, ToolCapability.Connect(_identity.ToolName), _identity.WorkspaceId);

        _connectionVersion++;
        _definition = definition;

        var context = new LifecycleContext
        {
            WorkspaceId = WorkspaceId.From(_identity.WorkspaceId),
            Phase = LifecyclePhase.ToolConnecting
        };

        await lifecycleManager.RunHooksAsync(LifecyclePhase.ToolConnecting, context, token.CancellationToken);

        var connector = discovery.GetConnector(definition.Type);
        token.CancellationToken.ThrowIfCancellationRequested();
        _handle = await connector.ConnectAsync(definition, token, token.CancellationToken);

        await lifecycleManager.RunHooksAsync(
            LifecyclePhase.ToolConnected,
            context with { Phase = LifecyclePhase.ToolConnected },
            token.CancellationToken);

        LogToolConnected(_identity.ToolName, _identity.WorkspaceId);
        return _handle;
    }

    public async Task DisconnectAsync()
    {
        if (_handle is null || _definition is null)
            return;

        _connectionVersion++;
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
        var request = invocation with
        {
            Parameters = new Dictionary<string, string>(invocation.Parameters, StringComparer.Ordinal)
        };
        token = token with { Grants = new HashSet<string>(token.Grants, StringComparer.Ordinal) };
        token.CancellationToken.ThrowIfCancellationRequested();
        var handle = _handle;
        var definition = _definition;
        var connectionVersion = _connectionVersion;
        var connector = definition is null ? null : discovery.GetConnector(definition.Type);
        if (connector is not null)
            request = connector.NormalizeInvocation(request);
        _identity.Ensure(invocation: request, token: token);
        if (string.IsNullOrWhiteSpace(request.Method))
            throw new UnauthorizedAccessException("A concrete tool operation is required.");
        var grant = ToolCapability.Invoke(_identity.ToolName, request.Method);
        await authorizer.AuthorizeAsync(token, grant, _identity.WorkspaceId);

        if (handle is null || definition is null || connector is null)
            throw new InvalidOperationException($"Tool '{_identity.ToolName}' is not connected");
        if (!string.Equals(handle.ToolName, _identity.ToolName, StringComparison.Ordinal) || handle.Type != definition.Type)
            throw new UnauthorizedAccessException("Tool connection does not match actor identity.");

        var blockedResult = await _leakGuard.BlockIfOutboundLeaksAsync(_identity.WorkspaceId, _identity.ToolName, request);
        if (blockedResult is not null)
            return blockedResult;

        var candidate = InvocationFingerprint.Prepare(request, token, definition.Type.ToString(), timeProvider.GetUtcNow());
        if (candidate is null)
            return new ToolResult
            {
                ToolName = _identity.ToolName,
                InvocationId = request.InvocationId,
                Outcome = InvocationOutcome.NotDispatched,
                ErrorCode = "invalid-invocation",
                Error = "Use a nonempty 32-hex invocation ID and at most 1048576 input characters with non-null parameter values."
            };
        request = request with { InvocationId = candidate.InvocationId };
        var effectiveInvocation = await _secretSubstitutor.SubstituteAsync(_identity.WorkspaceId, request);
        var binding = connector as IApprovalTargetBinding;
        var adapterTarget = binding?.GetApprovalTargetDigest(handle);
        if (binding is not null)
        {
            var effective = InvocationFingerprint.Prepare(effectiveInvocation, token, definition.Type.ToString(), candidate.CreatedAt);
            candidate = candidate with { ApprovalTargetDigest = InvocationApprovalPlan.BindTarget(adapterTarget, effective?.InputDigest) };
        }
        await RevalidateAsync();
        var admittedAt = timeProvider.GetUtcNow();
        candidate = candidate with { CreatedAt = admittedAt, Attempt = candidate.Attempt with { StartedAt = admittedAt } };
        var result = await _execution.ExecuteAsync(candidate, RevalidateAsync, async () =>
        {
            var response = await connector.InvokeAsync(handle, effectiveInvocation, token.CancellationToken);
            return await _leakGuard.RedactIfInboundLeaksAsync(_identity.WorkspaceId, _identity.ToolName, response);
        }, token.CancellationToken);

        if (!result.IsReplay && result.OutcomeRecorded)
        {
            try
            {
                await eventBus.PublishAsync(new ToolInvocationCompletedEvent
                {
                    SourceId = $"{_identity.WorkspaceId}/{_identity.ToolName}",
                    ToolName = _identity.ToolName,
                    WorkspaceId = WorkspaceId.From(_identity.WorkspaceId),
                    Success = result.Success,
                    Duration = result.Duration
                }, token.CancellationToken);
            }
            catch (Exception error)
            {
                // The journal is authoritative; this notification is not an outbox.
                LogCompletionNotificationFailure(result.InvocationId, error.GetType().Name);
            }
        }

        return result;

        async Task RevalidateAsync()
        {
            await authorizer.AuthorizeAsync(token, grant, _identity.WorkspaceId);
            token.CancellationToken.ThrowIfCancellationRequested();
            if (connectionVersion != _connectionVersion || !ReferenceEquals(handle, _handle) || !ReferenceEquals(definition, _definition))
                throw new UnauthorizedAccessException("Tool connection changed during authorization.");
            if (binding is not null && !string.Equals(adapterTarget, binding.GetApprovalTargetDigest(handle), StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Tool approval target changed during authorization.");
        }
    }

    public async Task<ToolSchema> GetSchemaAsync()
    {
        if (_handle is null || _definition is null)
            return new ToolSchema { ToolName = _identity.ToolName, Description = "Tool not connected" };

        var connector = discovery.GetConnector(_definition.Type);
        return await connector.DiscoverSchemaAsync(_handle);
    }

    public Task<ToolHandle?> GetHandleAsync() => Task.FromResult(_handle);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool '{Tool}' connected in workspace '{Workspace}'")]
    private partial void LogToolConnected(string tool, string workspace);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool '{Tool}' disconnected from workspace '{Workspace}'")]
    private partial void LogToolDisconnected(string tool, string workspace);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invocation {InvocationId} was recorded but its completion notification failed ({ErrorType})")]
    private partial void LogCompletionNotificationFailure(InvocationId? invocationId, string errorType);
}
