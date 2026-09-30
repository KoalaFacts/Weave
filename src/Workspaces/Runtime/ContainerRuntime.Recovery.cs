using Microsoft.Extensions.Logging;
using Weave.Shared.Ids;

namespace Weave.Workspaces.Runtime;

public sealed partial class ContainerRuntime
{
    public async Task<ContainerRuntimeCondition> ObserveContainerAsync(ContainerId containerId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id = containerId.ToString();
        if (!IsExactContainerId(id))
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

    public async Task<ContainerRecoveryResult> RecoverContainerAsync(ContainerId containerId, Func<Task> authorizeDispatch, CancellationToken ct)
    {
        var condition = await ObserveContainerAsync(containerId, ct);
        if (condition is not ContainerRuntimeCondition.Stopped)
            return new ContainerRecoveryResult
            {
                ContainerId = containerId.ToString(),
                Condition = condition,
                Outcome = condition is ContainerRuntimeCondition.Running
                    ? ContainerRecoveryOutcome.AlreadyRunning : ContainerRecoveryOutcome.Blocked
            };

        await authorizeDispatch();
        ct.ThrowIfCancellationRequested();
        try
        {
            var output = await RunContainerCliAsync(["start", containerId.ToString()], ct);
            if (output.Trim() != containerId.ToString())
                return UnknownRecovery(containerId, ContainerRuntimeCondition.Unknown);
            condition = await ObserveContainerAsync(containerId, ct);
            return new ContainerRecoveryResult
            {
                ContainerId = containerId.ToString(),
                Condition = condition,
                Dispatched = true,
                Outcome = condition is ContainerRuntimeCondition.Running
                    ? ContainerRecoveryOutcome.Started : ContainerRecoveryOutcome.OutcomeUnknown
            };
        }
        catch (Exception error) when (IsRuntimeFailure(error))
        {
            LogRuntimeRecoveryUnknown(logger, error.GetType().Name);
            return UnknownRecovery(containerId, ContainerRuntimeCondition.Unavailable);
        }
    }

    private static ContainerRecoveryResult UnknownRecovery(ContainerId containerId, ContainerRuntimeCondition condition) =>
        new() { ContainerId = containerId.ToString(), Condition = condition, Dispatched = true, Outcome = ContainerRecoveryOutcome.OutcomeUnknown };

    internal static bool IsExactContainerId(string id) =>
        id.Length == 64 && id.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    [LoggerMessage(Level = LogLevel.Warning, Message = "Container runtime observation unavailable ({ErrorType})")]
    private static partial void LogRuntimeObservationFailed(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Container recovery outcome unknown ({ErrorType})")]
    private static partial void LogRuntimeRecoveryUnknown(ILogger logger, string errorType);
}
