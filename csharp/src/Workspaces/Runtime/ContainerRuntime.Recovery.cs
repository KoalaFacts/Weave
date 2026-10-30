using Microsoft.Extensions.Logging;
using Weave.Shared.Ids;

namespace Weave.Workspaces.Runtime;

public sealed partial class ContainerRuntime
{
    public async Task<ContainerRuntimeCondition> ObserveContainerAsync(ContainerId containerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id = containerId.ToString();
        if (!IsExactEngineId(id))
            return ContainerRuntimeCondition.InvalidIdentity;

        try
        {
            var output = await RunContainerCliAsync(
                ["container", "ls", "--all", "--no-trunc", "--filter", $"id={id}", "--format", "{{.ID}}|{{.State}}"], ct);
            if (string.IsNullOrWhiteSpace(output))
                return ContainerRuntimeCondition.Missing;
            if (output.Length > 256)
                return ContainerRuntimeCondition.Unknown;
            var parts = output.Trim().Split('|');
            if (parts.Length != 2 || parts[0] != id)
                return ContainerRuntimeCondition.Unknown;
            return parts[1] switch
            {
                "running" => ContainerRuntimeCondition.Running,
                "exited" or "stopped" or "created" or "configured" => ContainerRuntimeCondition.Stopped,
                "paused" or "restarting" or "removing" => ContainerRuntimeCondition.Transitioning,
                _ => ContainerRuntimeCondition.Unknown
            };
        }
        catch (Exception error) when (IsRuntimeFailure(error))
        {
            LogRuntimeObservationFailed(logger, error.GetType().Name);
            return ContainerRuntimeCondition.Unavailable;
        }
    }

    public async Task<ContainerRecoveryResult> RecoverContainerAsync(ContainerId containerId, NetworkId requiredNetworkId, Func<Task> authorizeDispatch, CancellationToken ct)
    {
        var condition = await ObserveContainerAsync(containerId, ct);
        var result = new ContainerRecoveryResult
        {
            ContainerId = containerId.ToString(),
            Condition = condition,
            Network = new NetworkRuntimeObservation { NetworkId = requiredNetworkId.ToString() }
        };
        if (condition is not (ContainerRuntimeCondition.Stopped or ContainerRuntimeCondition.Running))
            return result;

        result = await ObserveRecoveryNetworkAsync(result, requiredNetworkId, ct);
        if (!NetworkReady(result))
            return result;
        if (condition is ContainerRuntimeCondition.Running)
            return result with { Outcome = ContainerRecoveryOutcome.AlreadyRunning };

        result = await ObserveRecoveryNetworkAsync(result, requiredNetworkId, ct);
        if (!NetworkReady(result))
            return result;

        await authorizeDispatch();
        ct.ThrowIfCancellationRequested();
        try
        {
            var output = await RunContainerCliAsync(["start", containerId.ToString()], ct);
            if (output.Trim() != containerId.ToString())
                return result with { Dispatched = true, Outcome = ContainerRecoveryOutcome.OutcomeUnknown, Condition = ContainerRuntimeCondition.Unknown };
            condition = await ObserveContainerAsync(containerId, ct);
            result = await ObserveRecoveryNetworkAsync(result with { Condition = condition, Dispatched = true }, requiredNetworkId, ct);
            return result with
            {
                Outcome = condition is ContainerRuntimeCondition.Running && NetworkReady(result)
                    ? ContainerRecoveryOutcome.Started : ContainerRecoveryOutcome.OutcomeUnknown
            };
        }
        catch (Exception error) when (IsRuntimeFailure(error))
        {
            LogRuntimeRecoveryUnknown(logger, error.GetType().Name);
            return result with { Dispatched = true, Outcome = ContainerRecoveryOutcome.OutcomeUnknown, Condition = ContainerRuntimeCondition.Unavailable };
        }
    }

    private static bool NetworkReady(ContainerRecoveryResult result) =>
        result.Network.Condition is NetworkRuntimeCondition.Present && result.NetworkAttachment is ContainerNetworkCondition.Attached;

    private static bool IsExactEngineId(string id) =>
        id.Length == 64 && id.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    [LoggerMessage(Level = LogLevel.Warning, Message = "Container runtime observation unavailable ({ErrorType})")]
    private static partial void LogRuntimeObservationFailed(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Container recovery outcome unknown ({ErrorType})")]
    private static partial void LogRuntimeRecoveryUnknown(ILogger logger, string errorType);
}
