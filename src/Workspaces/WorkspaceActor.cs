using Microsoft.Extensions.Logging;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;
using Weave.Tools.InstallDaprTool;
using Weave.Tools.InstallMcpTool;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Runtime;
using Weave.Workspaces.Templates;

namespace Weave.Workspaces.Lifecycle;

public sealed partial class WorkspaceActor(
    IWorkspaceRuntime runtime,
    ILifecycleManager lifecycleManager,
    IEventBus eventBus,
    TimeProvider timeProvider,
    ILogger<WorkspaceActor> logger,
    IActorState<WorkspaceState> persistentState) : IWorkspaceActor
{
    private string? _key;

    public async Task OnActivatedAsync(string? key, CancellationToken cancellationToken)
    {
        _key = key;
        await persistentState.ReadStateAsync(cancellationToken);
        if (persistentState.State.WorkspaceId.IsEmpty && !string.IsNullOrWhiteSpace(key))
        {
            persistentState.State.WorkspaceId = WorkspaceId.From(key!);
            await persistentState.WriteStateAsync(cancellationToken);
        }
    }

    public async Task<WorkspaceState> StartAsync(WorkspaceManifest manifest)
    {
        var dependencyErrors = ManifestParser.ValidateDaprToolDependencies(manifest)
            .Concat(ManifestParser.ValidateMcpToolDependencies(manifest)).ToList();
        if (dependencyErrors.Count > 0)
            throw new InvalidOperationException(string.Join(" ", dependencyErrors));

        foreach (var tool in manifest.Tools.Values.Where(static item => item.RequiresPlugin is not null))
        {
            var pluginName = tool.RequiresPlugin!;
            if ((string.Equals(tool.Type, "mcp", StringComparison.OrdinalIgnoreCase)
                    && persistentState.State.DaprToolInstallations.Any(item =>
                        string.Equals(item.PluginName, pluginName, StringComparison.OrdinalIgnoreCase)))
                || (string.Equals(tool.Type, "dapr", StringComparison.OrdinalIgnoreCase)
                    && persistentState.State.McpToolInstallations.Any(item =>
                        string.Equals(item.PluginName, pluginName, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException(
                    $"Plugin name '{pluginName}' was already used by another installation type; choose a new name.");
        }

        foreach (var tool in manifest.Tools.Values.Where(static item =>
            string.Equals(item.Type, "mcp", StringComparison.OrdinalIgnoreCase) && item.RequiresPlugin is not null))
        {
            var plugin = manifest.Plugins[tool.RequiresPlugin!];
            var digest = McpToolInstallation.ComputeConfigDigest(tool.Mcp!.Url!,
                plugin.Config["server_name"], plugin.Config["server_version"], plugin.Config["operation"]);
            var existing = persistentState.State.McpToolInstallations.SingleOrDefault(item =>
                string.Equals(item.PluginName, tool.RequiresPlugin, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && !string.Equals(existing.PluginName, tool.RequiresPlugin, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"MCP installation '{tool.RequiresPlugin}' differs in case from the installed name '{existing.PluginName}'.");
            if (existing is not null && !string.Equals(existing.ConfigDigest, digest, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"MCP installation '{tool.RequiresPlugin}' changed; use a new plugin name for the new revision.");
        }

        if (persistentState.State.Status is WorkspaceStatus.Running)
            return persistentState.State;

        persistentState.State.Status = WorkspaceStatus.Starting;

        var context = new LifecycleContext
        {
            WorkspaceId = persistentState.State.WorkspaceId,
            Phase = LifecyclePhase.WorkspaceStarting
        };

        try
        {
            await lifecycleManager.RunHooksAsync(LifecyclePhase.WorkspaceStarting, context, CancellationToken.None);

            var env = await runtime.ProvisionAsync(manifest, CancellationToken.None);

            persistentState.State.Status = WorkspaceStatus.Running;
            persistentState.State.StartedAt = timeProvider.GetUtcNow();
            persistentState.State.NetworkId = env.NetworkId;
            persistentState.State.Name = manifest.Name;
            persistentState.State.Containers.Clear();
            persistentState.State.ActiveAgents.Clear();
            persistentState.State.ActiveTools.Clear();
            persistentState.State.ActivePlugins.Clear();
            persistentState.State.ActiveAgents.AddRange(manifest.Agents.Keys);
            persistentState.State.ActiveTools.AddRange(manifest.Tools.Keys);
            persistentState.State.ActivePlugins.AddRange(manifest.Tools.Values
                .Where(static tool => tool.RequiresPlugin is not null
                    && (string.Equals(tool.Type, "dapr", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tool.Type, "mcp", StringComparison.OrdinalIgnoreCase)))
                .Select(static tool => tool.RequiresPlugin!)
                .Distinct(StringComparer.Ordinal));
            foreach (var installation in persistentState.State.DaprToolInstallations)
                installation.DesiredEnabled = false;
            foreach (var installation in persistentState.State.McpToolInstallations)
                installation.DesiredEnabled = false;
            foreach (var pluginName in persistentState.State.ActivePlugins.Where(name =>
                string.Equals(manifest.Plugins[name].Type, "dapr_tools", StringComparison.OrdinalIgnoreCase)))
            {
                var port = int.Parse(manifest.Plugins[pluginName].Config["port"], System.Globalization.CultureInfo.InvariantCulture);
                var id = $"{persistentState.State.WorkspaceId}/{pluginName}";
                var installation = persistentState.State.DaprToolInstallations
                    .SingleOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
                if (installation is null)
                {
                    installation = new DaprToolInstallation { Id = id, PluginName = pluginName };
                    persistentState.State.DaprToolInstallations.Add(installation);
                }
                installation.Port = port;
                installation.ConfigDigest = DaprToolInstallation.ComputeConfigDigest(port);
                installation.DesiredEnabled = true;
            }
            foreach (var tool in manifest.Tools.Values.Where(static item =>
                string.Equals(item.Type, "mcp", StringComparison.OrdinalIgnoreCase) && item.RequiresPlugin is not null))
            {
                var pluginName = tool.RequiresPlugin!;
                var plugin = manifest.Plugins[pluginName];
                var url = tool.Mcp!.Url!;
                var serverName = plugin.Config["server_name"];
                var serverVersion = plugin.Config["server_version"];
                var operation = plugin.Config["operation"];
                var digest = McpToolInstallation.ComputeConfigDigest(url, serverName, serverVersion, operation);
                var id = $"{persistentState.State.WorkspaceId}/{pluginName}";
                var installation = persistentState.State.McpToolInstallations.SingleOrDefault(item =>
                    string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                if (installation is null)
                {
                    installation = new McpToolInstallation { Id = id, PluginName = pluginName };
                    persistentState.State.McpToolInstallations.Add(installation);
                }
                installation.Url = url;
                installation.ServerName = serverName;
                installation.ServerVersion = serverVersion;
                installation.Operation = operation;
                installation.ConfigDigest = digest;
                installation.DesiredEnabled = true;
            }
            foreach (var container in env.Containers)
            {
                persistentState.State.Containers.Add(new ContainerInfo
                {
                    ContainerId = container.ContainerId,
                    Name = container.Name,
                    Image = container.Image,
                    Status = ContainerStatus.Running
                });
            }

            await persistentState.WriteStateAsync();

            await lifecycleManager.RunHooksAsync(
                LifecyclePhase.WorkspaceStarted,
                context with { Phase = LifecyclePhase.WorkspaceStarted },
                CancellationToken.None);

            await eventBus.PublishAsync(new WorkspaceStartedEvent
            {
                SourceId = persistentState.State.WorkspaceId,
                WorkspaceName = manifest.Name,
                AgentNames = [.. manifest.Agents.Keys]
            }, CancellationToken.None);

            LogWorkspaceStarted(persistentState.State.WorkspaceId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or HttpRequestException or System.ComponentModel.Win32Exception)
        {
            persistentState.State.Status = WorkspaceStatus.Error;
            persistentState.State.ErrorMessage = ex.Message;
            await persistentState.WriteStateAsync();
            LogWorkspaceFailed(ex, persistentState.State.WorkspaceId, "start");
            throw;
        }

        return persistentState.State;
    }

    public async Task StopAsync()
    {
        if (persistentState.State.Status is not WorkspaceStatus.Running)
            return;

        persistentState.State.Status = WorkspaceStatus.Stopping;

        var context = new LifecycleContext
        {
            WorkspaceId = persistentState.State.WorkspaceId,
            Phase = LifecyclePhase.WorkspaceStopping
        };

        try
        {
            await lifecycleManager.RunHooksAsync(LifecyclePhase.WorkspaceStopping, context, CancellationToken.None);
            await runtime.TeardownAsync(persistentState.State.WorkspaceId, CancellationToken.None);

            persistentState.State.Status = WorkspaceStatus.Stopped;
            persistentState.State.StoppedAt = timeProvider.GetUtcNow();
            persistentState.State.Containers.Clear();
            persistentState.State.ActiveAgents.Clear();
            persistentState.State.ActiveTools.Clear();
            persistentState.State.ActivePlugins.Clear();
            foreach (var installation in persistentState.State.DaprToolInstallations)
                installation.DesiredEnabled = false;
            foreach (var installation in persistentState.State.McpToolInstallations)
                installation.DesiredEnabled = false;
            persistentState.State.NetworkId = null;

            await persistentState.WriteStateAsync();

            await lifecycleManager.RunHooksAsync(
                LifecyclePhase.WorkspaceStopped,
                context with { Phase = LifecyclePhase.WorkspaceStopped },
                CancellationToken.None);

            await eventBus.PublishAsync(new WorkspaceStoppedEvent
            {
                SourceId = persistentState.State.WorkspaceId
            }, CancellationToken.None);

            LogWorkspaceStopped(persistentState.State.WorkspaceId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or HttpRequestException or System.ComponentModel.Win32Exception)
        {
            persistentState.State.Status = WorkspaceStatus.Error;
            persistentState.State.ErrorMessage = ex.Message;
            await persistentState.WriteStateAsync();
            LogWorkspaceFailed(ex, persistentState.State.WorkspaceId, "stop");
            throw;
        }
    }

    public async Task SetDaprToolInstallationEnabledAsync(string pluginName, bool enabled)
    {
        var installation = persistentState.State.DaprToolInstallations.SingleOrDefault(item =>
            string.Equals(item.PluginName, pluginName, StringComparison.Ordinal));
        if (installation is null)
            throw new InvalidOperationException($"Dapr tool installation '{pluginName}' was not found.");
        if (enabled && persistentState.State.Status is not WorkspaceStatus.Running)
            throw new InvalidOperationException("The workspace must be running to enable a Dapr tool installation.");
        installation.DesiredEnabled = enabled;
        await persistentState.WriteStateAsync();
    }

    public async Task SetMcpToolInstallationEnabledAsync(string pluginName, bool enabled)
    {
        var installation = persistentState.State.McpToolInstallations.SingleOrDefault(item =>
            string.Equals(item.PluginName, pluginName, StringComparison.Ordinal));
        if (installation is null)
            throw new InvalidOperationException($"MCP tool installation '{pluginName}' was not found.");
        if (enabled && persistentState.State.Status is not WorkspaceStatus.Running)
            throw new InvalidOperationException("The workspace must be running to enable an MCP tool installation.");
        if (enabled && string.IsNullOrEmpty(installation.ContractDigest))
            throw new InvalidOperationException("The MCP tool contract has not been pinned.");
        installation.DesiredEnabled = enabled;
        await persistentState.WriteStateAsync();
    }

    public async Task PinMcpToolContractAsync(string pluginName, string contractDigest)
    {
        if (string.IsNullOrWhiteSpace(contractDigest))
            throw new InvalidOperationException("The MCP tool contract digest is required.");
        var installation = persistentState.State.McpToolInstallations.SingleOrDefault(item =>
            string.Equals(item.PluginName, pluginName, StringComparison.Ordinal));
        if (installation is null || persistentState.State.Status is not WorkspaceStatus.Running)
            throw new InvalidOperationException("The MCP tool installation is not running.");
        if (installation.ContractDigest.Length != 0
            && !string.Equals(installation.ContractDigest, contractDigest, StringComparison.Ordinal))
            throw new InvalidOperationException("The MCP tool contract changed; disable and review this installation.");
        installation.ContractDigest = contractDigest;
        await persistentState.WriteStateAsync();
    }

    public Task<WorkspaceState> GetStateAsync() => Task.FromResult(persistentState.State);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace {WorkspaceId} started")]
    private partial void LogWorkspaceStarted(WorkspaceId workspaceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace {WorkspaceId} stopped")]
    private partial void LogWorkspaceStopped(WorkspaceId workspaceId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to {Operation} workspace {WorkspaceId}")]
    private partial void LogWorkspaceFailed(Exception ex, WorkspaceId workspaceId, string operation);
}
