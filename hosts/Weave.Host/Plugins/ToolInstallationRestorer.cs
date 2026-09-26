using Weave.Security.Tokens;
using Weave.Tools.InstallDaprTool;
using Weave.Tools.InstallMcpTool;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Registry;
using Weave.Workspaces.Templates;

namespace Weave.Silo.Plugins;

internal sealed class ToolInstallationRestorer(
    IHostApplicationLifetime lifetime,
    IVirtualActorProvider actors,
    IPluginRegistry plugins,
    ICapabilityTokenService tokenService,
    ILogger<ToolInstallationRestorer> logger) : BackgroundService
{
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => _completed.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RestoreAsync(stoppingToken);
            _completed.TrySetResult();
        }
        catch (Exception error)
        {
            _completed.TrySetException(error);
            throw;
        }
    }

    private async Task RestoreAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);

        var registry = actors.GetActor<IWorkspaceRegistryActor>(VirtualActorId.From("active"));
        foreach (var workspaceId in await registry.GetWorkspaceIdsAsync())
        {
            stoppingToken.ThrowIfCancellationRequested();
            var workspace = actors.GetActor<IWorkspaceActor>(VirtualActorId.From(workspaceId));
            var state = await workspace.GetStateAsync();
            if (state.Status is not WorkspaceStatus.Running)
                continue;

            foreach (var installation in state.DaprToolInstallations.Where(item => item.DesiredEnabled))
            {
                if (!string.Equals(installation.Id, $"{workspaceId}/{installation.PluginName}", StringComparison.Ordinal)
                    || installation.Port is < 1 or > 65535
                    || !string.Equals(installation.ConfigDigest,
                        DaprToolInstallation.ComputeConfigDigest(installation.Port), StringComparison.Ordinal))
                {
                    logger.LogWarning("Dapr tool installation {InstallationId} has invalid stored configuration.", installation.Id);
                    continue;
                }
                using var source = tokenService.MintLinked(new CapabilityTokenRequest
                {
                    WorkspaceId = workspaceId,
                    IssuedTo = $"{workspaceId}/installation-restorer",
                    Grants = [$"plugin:invoke:{installation.Id}"],
                    Lifetime = TimeSpan.FromMinutes(1)
                }, stoppingToken);
                var status = await plugins.ConnectAsync(installation.Id, new PluginDefinition
                {
                    Type = "dapr_tools",
                    Config = new Dictionary<string, string>
                    {
                        ["port"] = installation.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }
                }, source.Token);
                if (!status.IsConnected)
                    logger.LogWarning("Dapr tool installation {InstallationId} could not reactivate: {Error}",
                        installation.Id, status.Error);
                else
                {
                    var current = await workspace.GetStateAsync();
                    if (current.Status is not WorkspaceStatus.Running
                        || !current.DaprToolInstallations.Any(item =>
                            string.Equals(item.Id, installation.Id, StringComparison.Ordinal)
                            && item.DesiredEnabled
                            && string.Equals(item.ConfigDigest, installation.ConfigDigest, StringComparison.Ordinal)))
                        await plugins.DisconnectAsync(installation.Id, source.Token);
                }
            }

            foreach (var installation in state.McpToolInstallations.Where(item => item.DesiredEnabled))
            {
                if (!string.Equals(installation.Id, $"{workspaceId}/{installation.PluginName}", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(installation.ContractDigest)
                    || !string.Equals(installation.ConfigDigest,
                        McpToolInstallation.ComputeConfigDigest(installation.Url, installation.ServerName,
                            installation.ServerVersion, installation.Operation), StringComparison.Ordinal))
                {
                    logger.LogWarning("MCP installation {InstallationId} has invalid stored configuration.", installation.Id);
                    continue;
                }
                using var source = tokenService.MintLinked(new CapabilityTokenRequest
                {
                    WorkspaceId = workspaceId,
                    IssuedTo = $"{workspaceId}/installation-restorer",
                    Grants = [$"plugin:invoke:{installation.Id}"],
                    Lifetime = TimeSpan.FromMinutes(1)
                }, stoppingToken);
                var status = await plugins.ConnectAsync(installation.Id, new PluginDefinition
                {
                    Type = "mcp_tools",
                    Config = new Dictionary<string, string>
                    {
                        ["url"] = installation.Url,
                        ["server_name"] = installation.ServerName,
                        ["server_version"] = installation.ServerVersion,
                        ["operation"] = installation.Operation,
                        ["contract_digest"] = installation.ContractDigest
                    }
                }, source.Token);
                if (!status.IsConnected)
                    logger.LogWarning("MCP installation {InstallationId} could not reactivate: {Error}",
                        installation.Id, status.Error);
                else
                {
                    var current = await workspace.GetStateAsync();
                    if (current.Status is not WorkspaceStatus.Running
                        || !current.McpToolInstallations.Any(item =>
                            string.Equals(item.Id, installation.Id, StringComparison.Ordinal)
                            && item.DesiredEnabled
                            && string.Equals(item.ConfigDigest, installation.ConfigDigest, StringComparison.Ordinal)
                            && string.Equals(item.ContractDigest, installation.ContractDigest, StringComparison.Ordinal)))
                        await plugins.DisconnectAsync(installation.Id, source.Token);
                }
            }
        }
    }
}
