using Microsoft.Extensions.Logging;
using Weave.Shared.Ids;

namespace Weave.Workspaces.Runtime;

public sealed partial class ContainerRuntime
{
    public async Task<NetworkRuntimeCondition> ObserveNetworkAsync(NetworkId networkId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id = networkId.ToString();
        if (!IsExactEngineId(id))
            return NetworkRuntimeCondition.InvalidIdentity;
        try
        {
            var output = await RunContainerCliAsync(
                ["network", "ls", "--no-trunc", "--filter", $"id={id}", "--format", "{{.ID}}"], ct);
            return string.IsNullOrWhiteSpace(output) ? NetworkRuntimeCondition.Missing
                : output.Length <= 66 && output.Trim() == id ? NetworkRuntimeCondition.Present
                : NetworkRuntimeCondition.Unknown;
        }
        catch (Exception error) when (IsRuntimeFailure(error))
        {
            LogNetworkObservationFailed(logger, error.GetType().Name);
            return NetworkRuntimeCondition.Unavailable;
        }
    }

    public async Task<ContainerNetworkCondition> ObserveContainerNetworkAsync(ContainerId containerId, NetworkId networkId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsExactEngineId(containerId.ToString()) || !IsExactEngineId(networkId.ToString()))
            return ContainerNetworkCondition.InvalidIdentity;
        try
        {
            var output = await RunContainerCliAsync(["container", "inspect", "--format",
                "{{.Id}}|{{range .NetworkSettings.Networks}}{{.NetworkID}} {{end}}", containerId.ToString()], ct);
            if (output.Length > 4096)
                return ContainerNetworkCondition.Unknown;
            var parts = output.Trim().Split('|');
            if (parts.Length != 2 || parts[0] != containerId.ToString())
                return ContainerNetworkCondition.Unknown;
            var networks = parts[1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (networks.Any(static id => !IsExactEngineId(id)) || networks.Distinct(StringComparer.Ordinal).Count() != networks.Length)
                return ContainerNetworkCondition.Unknown;
            return networks.Contains(networkId.ToString(), StringComparer.Ordinal)
                ? ContainerNetworkCondition.Attached : ContainerNetworkCondition.Detached;
        }
        catch (Exception error) when (IsRuntimeFailure(error))
        {
            LogNetworkObservationFailed(logger, error.GetType().Name);
            return ContainerNetworkCondition.Unavailable;
        }
    }

    private async Task<ContainerRecoveryResult> ObserveRecoveryNetworkAsync(ContainerRecoveryResult result, NetworkId networkId, CancellationToken ct)
    {
        var network = new NetworkRuntimeObservation { NetworkId = networkId.ToString(), Condition = await ObserveNetworkAsync(networkId, ct) };
        return result with
        {
            Network = network,
            NetworkAttachment = network.Condition is NetworkRuntimeCondition.Present
                ? await ObserveContainerNetworkAsync(ContainerId.From(result.ContainerId), networkId, ct)
                : ContainerNetworkCondition.NotChecked
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Container network observation unavailable ({ErrorType})")]
    private static partial void LogNetworkObservationFailed(ILogger logger, string errorType);
}
