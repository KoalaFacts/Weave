using Weave.Agents.Channels;
using Weave.Agents.Heartbeat;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Security.Tokens;
using Weave.Shared.Cqrs;
using Weave.Silo.Plugins;
using Weave.Workspaces.Lifecycle;
using Weave.Workspaces.Registry;
using Weave.Workspaces.Runtime;
using Weave.Workspaces.Templates;

namespace Weave.Silo.Api;

public sealed class StopWorkspaceHandler(
    IVirtualActorProvider actors,
    IPluginRegistry plugins,
    ICapabilityTokenService tokenService,
    IMcpInstallationDispatchGate mcpDispatchGate,
    IInstallationDiagnostics diagnostics,
    IWorkspaceRuntime runtime)
    : ICommandHandler<StopWorkspaceCommand, bool>
{
    public async Task<bool> HandleAsync(StopWorkspaceCommand command, CancellationToken ct)
    {
        var workspaceId = command.WorkspaceId.ToString();
        var workspace = actors.GetActor<IWorkspaceActor>(VirtualActorId.From(command.WorkspaceId.ToString()));
        var state = await workspace.GetStateAsync();

        var retryingTeardown = state.Status is WorkspaceStatus.Error or WorkspaceStatus.Stopping
            && (state.NetworkId is not null || state.Containers.Count > 0);
        if ((state.Status is WorkspaceStatus.Running || retryingTeardown)
            && !string.Equals(state.RuntimeName, runtime.RuntimeName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The workspace's creating runtime does not match this Host; restore it before stopping.");

        if (!retryingTeardown)
        {
            foreach (var installation in state.McpToolInstallations.Where(item => item.DesiredEnabled))
            {
                mcpDispatchGate.BeginDisable(installation.Id);
                await workspace.SetMcpToolInstallationEnabledAsync(installation.PluginName, false);
            }

            foreach (var agentName in state.ActiveAgents)
            {
                var heartbeat = actors.GetActor<IHeartbeatActor>(VirtualActorId.Combine(command.WorkspaceId, agentName));
                await heartbeat.StopAsync();
            }

            var supervisor = actors.GetActor<IAgentSupervisorActor>(VirtualActorId.From(command.WorkspaceId.ToString()));
            await supervisor.DeactivateAllAsync();

            var toolRegistry = actors.GetActor<IToolRegistryActor>(VirtualActorId.From(command.WorkspaceId.ToString()));
            await toolRegistry.DisconnectAllAsync();

            foreach (var pluginName in state.ActivePlugins)
            {
                if (state.DaprToolInstallations.Any(item => string.Equals(item.PluginName, pluginName, StringComparison.Ordinal)))
                    await workspace.SetDaprToolInstallationEnabledAsync(pluginName, false);
                var registrationName = $"{workspaceId}/{pluginName}";
                using var source = tokenService.MintLinked(new CapabilityTokenRequest
                {
                    WorkspaceId = workspaceId,
                    IssuedTo = $"{workspaceId}/workspace-stop",
                    Grants = [$"plugin:invoke:{registrationName}"],
                    Lifetime = TimeSpan.FromMinutes(1)
                }, CancellationToken.None);
                var plugin = await plugins.DisconnectAsync(registrationName, source.Token);
                if (plugin.InstallationFailure == InstallationFailureCode.DependencyInUse)
                    throw new InvalidOperationException(plugin.Error);
                diagnostics.Record(registrationName, InstallationFailureCode.None);
            }
        }

        await workspace.StopAsync();

        var registry = actors.GetActor<IWorkspaceRegistryActor>(VirtualActorId.From("active"));
        await registry.UnregisterAsync(workspaceId);

        return true;
    }
}
