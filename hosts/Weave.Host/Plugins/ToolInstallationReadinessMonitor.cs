using Weave.Plugins;
using Weave.Tools.InstallDaprTool;
using Weave.Tools.InstallMcpTool;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Registry;
using Weave.Workspaces.Templates;

namespace Weave.Silo.Plugins;

internal sealed class ToolInstallationReadinessMonitor(
    IHostApplicationLifetime lifetime,
    IVirtualActorProvider actors,
    IToolInstallationPeerProbe probe,
    IInstallationDiagnostics diagnostics,
    TimeProvider timeProvider,
    ILogger<ToolInstallationReadinessMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private readonly TaskCompletionSource _timerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task TimerReady => _timerReady.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);
        using var timer = new PeriodicTimer(Interval, timeProvider);
        while (true)
        {
            try
            {
                var nextTick = timer.WaitForNextTickAsync(stoppingToken);
                _timerReady.TrySetResult();
                if (!await nextTick)
                    break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            try
            {
                await ProbeOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning("Installation readiness cycle failed ({ErrorType}).", error.GetType().Name);
            }
        }
    }

    public async Task ProbeOnceAsync(CancellationToken cancellationToken)
    {
        var registry = actors.GetActor<IWorkspaceRegistryActor>(VirtualActorId.From("active"));
        foreach (var workspaceId in await registry.GetWorkspaceIdsAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var workspace = actors.GetActor<IWorkspaceActor>(VirtualActorId.From(workspaceId));
                var state = await workspace.GetStateAsync();
                if (state.Status is not WorkspaceStatus.Running)
                    continue;

                foreach (var installation in state.DaprToolInstallations.Where(item => CanProbe(workspaceId, item)))
                    await ProbeInstallationAsync(workspace, installation,
                        token => probe.ProbeAsync(installation, token),
                        current => current.DaprToolInstallations.FirstOrDefault(item => item.Id == installation.Id),
                        cancellationToken);
                foreach (var installation in state.McpToolInstallations.Where(item => CanProbe(workspaceId, item)))
                    await ProbeInstallationAsync(workspace, installation,
                        token => probe.ProbeAsync(installation, token),
                        current => current.McpToolInstallations.FirstOrDefault(item => item.Id == installation.Id),
                        cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                logger.LogWarning("Workspace installation readiness probes failed ({ErrorType}).",
                    error.GetType().Name);
            }
        }
    }

    private async Task ProbeInstallationAsync<TInstallation>(IWorkspaceActor workspace, TInstallation installation,
        Func<CancellationToken, Task<InstallationFailureCode>> probePeer,
        Func<WorkspaceState, TInstallation?> currentInstallation, CancellationToken cancellationToken)
        where TInstallation : PluginInstallation
    {
        using var timeout = new CancellationTokenSource(ProbeTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        InstallationFailureCode failure;
        try
        {
            failure = await probePeer(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            failure = InstallationFailureCode.PeerUnavailable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            logger.LogWarning("Installation {InstallationId} probe failed ({ErrorType}).",
                installation.Id, error.GetType().Name);
            failure = InstallationFailureCode.ConnectionFailed;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var current = await workspace.GetStateAsync();
        if (current.Status is not WorkspaceStatus.Running)
            return;
        var latest = currentInstallation(current);
        if (latest is null || !CanProbe(current.WorkspaceId.ToString(), latest)
            || latest.DefinitionRevision != installation.DefinitionRevision
            || latest.ConfigDigest != installation.ConfigDigest
            || latest is McpToolInstallation currentMcp && installation is McpToolInstallation originalMcp
                && currentMcp.ContractDigest != originalMcp.ContractDigest)
            return;
        diagnostics.RecordProbe(latest, failure);
    }

    private static bool CanProbe(string workspaceId, PluginInstallation installation)
    {
        if (!installation.DesiredEnabled || installation.HasUnsupportedAuthority()
            || string.IsNullOrWhiteSpace(installation.PluginName)
            || !string.Equals(installation.Id, $"{workspaceId}/{installation.PluginName}", StringComparison.Ordinal))
            return false;
        return installation switch
        {
            DaprToolInstallation dapr =>
                dapr.DefinitionRevision == DaprToolInstallation.ImplementationRevision
                && dapr.Port is >= 1 and <= 65535
                && dapr.ConfigDigest == DaprToolInstallation.ComputeConfigDigest(dapr.Port),
            McpToolInstallation mcp =>
                mcp.DefinitionRevision == McpToolInstallation.ImplementationRevision
                && mcp.ConfigDigest == McpToolInstallation.ComputeConfigDigest(
                    mcp.Url, mcp.ServerName, mcp.ServerVersion, mcp.Operation)
                && mcp.ContractDigest.Length == 64 && mcp.ContractDigest.All(Uri.IsHexDigit)
                && !string.IsNullOrWhiteSpace(mcp.ServerName)
                && !string.IsNullOrWhiteSpace(mcp.ServerVersion)
                && !string.IsNullOrWhiteSpace(mcp.Operation)
                && Uri.TryCreate(mcp.Url, UriKind.Absolute, out var endpoint)
                && endpoint.Scheme == Uri.UriSchemeHttp && endpoint.Host == "127.0.0.1"
                && endpoint.Port is >= 1 and <= 65535 && endpoint.AbsolutePath == "/mcp"
                && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0 && endpoint.UserInfo.Length == 0,
            _ => false
        };
    }
}
