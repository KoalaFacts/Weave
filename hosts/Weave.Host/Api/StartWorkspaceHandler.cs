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
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Registry;
using Weave.Workspaces.Templates;

namespace Weave.Silo.Api;

public sealed class StartWorkspaceHandler(
    IVirtualActorProvider actors,
    IPluginRegistry plugins,
    ICapabilityTokenService tokenService)
    : ICommandHandler<StartWorkspaceCommand, WorkspaceState>
{
    public async Task<WorkspaceState> HandleAsync(StartWorkspaceCommand command, CancellationToken ct)
    {
        var errors = ManifestParser.ValidateDaprToolDependencies(command.Manifest)
            .Concat(ManifestParser.ValidateMcpToolDependencies(command.Manifest)).ToList();
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(" ", errors));

        var workspaceId = command.WorkspaceId.ToString();
        var workspace = actors.GetActor<IWorkspaceActor>(VirtualActorId.From(command.WorkspaceId.ToString()));
        var registry = actors.GetActor<IWorkspaceRegistryActor>(VirtualActorId.From("active"));
        if ((await workspace.GetStateAsync()).Status is WorkspaceStatus.Running)
            throw new InvalidOperationException("Workspace is already running; stop it before changing the manifest.");

        var toolRegistry = actors.GetActor<IToolRegistryActor>(VirtualActorId.From(command.WorkspaceId.ToString()));
        var supervisor = actors.GetActor<IAgentSupervisorActor>(VirtualActorId.From(command.WorkspaceId.ToString()));
        var requiredPlugins = command.Manifest.Tools.Values
            .Where(static tool => tool.RequiresPlugin is not null
                && (string.Equals(tool.Type, "dapr", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(tool.Type, "mcp", StringComparison.OrdinalIgnoreCase)))
            .Select(static tool => tool.RequiresPlugin!)
            .Distinct(StringComparer.Ordinal);
        var activated = new List<string>();
        var startedHeartbeats = new List<string>();
        try
        {
            await registry.RegisterAsync(workspaceId);
            await workspace.StartAsync(command.Manifest);

            var installations = (await workspace.GetStateAsync()).McpToolInstallations;

            foreach (var pluginName in requiredPlugins)
            {
                var registrationName = $"{workspaceId}/{pluginName}";
                var definition = command.Manifest.Plugins[pluginName];
                var installation = installations.SingleOrDefault(item =>
                    string.Equals(item.Id, registrationName, StringComparison.Ordinal) && item.DesiredEnabled);
                if (installation is not null)
                {
                    var config = new Dictionary<string, string>(definition.Config, StringComparer.Ordinal)
                    {
                        ["url"] = installation.Url
                    };
                    if (installation.ContractDigest.Length != 0)
                        config["contract_digest"] = installation.ContractDigest;
                    definition = definition with { Config = config };
                }
                using var source = tokenService.MintLinked(new CapabilityTokenRequest
                {
                    WorkspaceId = workspaceId,
                    IssuedTo = $"{workspaceId}/workspace-start",
                    Grants = [$"plugin:invoke:{registrationName}"],
                    Lifetime = TimeSpan.FromMinutes(1)
                }, ct);
                var plugin = await plugins.ConnectAsync(registrationName,
                    definition, source.Token);
                if (!plugin.IsConnected)
                    throw new InvalidOperationException($"Plugin '{pluginName}' could not activate: {plugin.Error}");
                activated.Add(registrationName);
                if (installation is not null)
                {
                    if (!plugin.Info.TryGetValue("contract_digest", out var observed))
                        throw new InvalidOperationException("MCP plugin did not report its observed contract.");
                    await workspace.PinMcpToolContractAsync(pluginName, observed);
                }
            }

            await toolRegistry.ConnectToolsAsync(command.Manifest.Tools);
            await toolRegistry.ConfigureAccessAsync(
                command.Manifest.Agents.ToDictionary(
                    static kvp => kvp.Key,
                    static kvp => kvp.Value.Tools.ToList(),
                    StringComparer.Ordinal),
                command.Manifest.Agents.ToDictionary(
                    static kvp => kvp.Key,
                    static kvp => (kvp.Value.Capabilities ?? []).ToList(),
                    StringComparer.Ordinal));

            await supervisor.ActivateAllAsync(command.Manifest);

            foreach (var (agentName, definition) in command.Manifest.Agents)
            {
                if (definition.Heartbeat is null)
                    continue;

                var heartbeat = actors.GetActor<IHeartbeatActor>(VirtualActorId.Combine(command.WorkspaceId, agentName));
                startedHeartbeats.Add(agentName);
                await heartbeat.StartAsync(new Weave.Agents.Heartbeat.HeartbeatConfig
                {
                    Cron = definition.Heartbeat.Cron,
                    Tasks = definition.Heartbeat.Tasks,
                    Enabled = true
                });
            }

            return await workspace.GetStateAsync();
        }
        catch (Exception startError)
        {
            var cleanupErrors = new List<Exception>();
            foreach (var agentName in startedHeartbeats.AsEnumerable().Reverse())
            {
                try
                {
                    var heartbeat = actors.GetActor<IHeartbeatActor>(VirtualActorId.Combine(command.WorkspaceId, agentName));
                    await heartbeat.StopAsync();
                }
                catch (Exception error)
                {
                    cleanupErrors.Add(error);
                }
            }

            try
            {
                await supervisor.DeactivateAllAsync();
            }
            catch (Exception error)
            {
                cleanupErrors.Add(error);
            }

            try
            {
                await toolRegistry.DisconnectAllAsync();
            }
            catch (Exception error)
            {
                cleanupErrors.Add(error);
            }

            foreach (var registrationName in activated.AsEnumerable().Reverse())
            {
                try
                {
                    using var source = tokenService.MintLinked(new CapabilityTokenRequest
                    {
                        WorkspaceId = workspaceId,
                        IssuedTo = $"{workspaceId}/workspace-start",
                        Grants = [$"plugin:invoke:{registrationName}"],
                        Lifetime = TimeSpan.FromMinutes(1)
                    }, CancellationToken.None);
                    await plugins.DisconnectAsync(registrationName, source.Token);
                }
                catch (Exception error)
                {
                    cleanupErrors.Add(error);
                }
            }

            try
            {
                await workspace.StopAsync();
            }
            catch (Exception error)
            {
                cleanupErrors.Add(error);
            }

            try
            {
                await registry.UnregisterAsync(workspaceId);
            }
            catch (Exception error)
            {
                cleanupErrors.Add(error);
            }

            if (cleanupErrors.Count > 0)
                throw new AggregateException("Workspace activation and cleanup failed.", [startError, .. cleanupErrors]);
            throw;
        }
    }
}
