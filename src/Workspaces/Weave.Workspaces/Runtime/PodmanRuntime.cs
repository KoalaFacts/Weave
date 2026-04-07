using Microsoft.Extensions.Logging;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Runtime;

public sealed partial class PodmanRuntime(ICommandRunner runner, ILogger<PodmanRuntime> logger) : IWorkspaceRuntime
{
    private readonly ILogger _logger = logger;

    public string RuntimeName => "podman";

    public async Task<WorkspaceEnvironment> ProvisionAsync(WorkspaceManifest manifest, CancellationToken ct)
    {
        var workspaceId = manifest.Name;

        // Create network
        var networkName = manifest.Workspace.Network?.Name?.Replace("{workspace}", workspaceId)
            ?? $"weave-{workspaceId}";
        var network = await CreateNetworkAsync(new NetworkSpec
        {
            Name = networkName,
            Subnet = manifest.Workspace.Network?.Subnet
        }, ct);

        // Start tool containers
        var containers = new List<ContainerHandle>();
        foreach (var (toolName, tool) in manifest.Tools)
        {
            if (tool.Type is not "mcp" || tool.Mcp is null)
                continue;

            var container = await StartContainerAsync(new ContainerSpec
            {
                Name = $"weave-{workspaceId}-{toolName}",
                Image = tool.Mcp.Server,
                Environment = tool.Mcp.Env,
                NetworkId = network.NetworkId,
                Command = tool.Mcp.Args
            }, ct);

            containers.Add(container);
        }

        LogWorkspaceProvisioned(workspaceId, containers.Count);

        return new WorkspaceEnvironment(WorkspaceId.From(workspaceId), network.NetworkId, containers);
    }

    public async Task TeardownAsync(WorkspaceId workspaceId, CancellationToken ct)
    {
        // Stop all containers in the workspace
        var output = await RunPodmanAsync(["ps", "--filter", $"name=weave-{workspaceId}", "--format", "{{.ID}}"], ct);
        var containerIds = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var id in containerIds)
        {
            await StopContainerAsync(ContainerId.From(id.Trim()), ct);
        }

        // Remove network
        await RunPodmanAsync(["network", "rm", "-f", $"weave-{workspaceId}"], ct);

        LogWorkspaceTornDown(workspaceId.ToString());
    }

    public async Task<ContainerHandle> StartContainerAsync(ContainerSpec spec, CancellationToken ct)
    {
        var args = new List<string> { "run", "-d", "--name", spec.Name };

        if (spec.NetworkId is not null)
            args.AddRange(["--network", spec.NetworkId.Value.ToString()]);

        if (spec.ReadOnly)
            args.Add("--read-only");

        if (spec.DropAllCapabilities)
            args.Add("--cap-drop=ALL");

        if (spec.NoNetwork)
            args.Add("--network=none");

        foreach (var (key, value) in spec.Environment)
            args.AddRange(["-e", $"{key}={value}"]);

        foreach (var (hostPort, containerPort) in spec.PortMappings)
            args.AddRange(["-p", $"{hostPort}:{containerPort}"]);

        args.Add(spec.Image);
        args.AddRange(spec.Command);

        var output = await RunPodmanAsync(args, ct);
        var containerId = ContainerId.From(output.Trim());

        return new ContainerHandle(containerId, spec.Name, spec.Image, spec.PortMappings);
    }

    public async Task StopContainerAsync(ContainerId containerId, CancellationToken ct)
    {
        await RunPodmanAsync(["stop", containerId.ToString()], ct);
        await RunPodmanAsync(["rm", "-f", containerId.ToString()], ct);
    }

    public async Task<NetworkHandle> CreateNetworkAsync(NetworkSpec spec, CancellationToken ct)
    {
        var args = new List<string> { "network", "create" };

        if (spec.Subnet is not null)
            args.AddRange(["--subnet", spec.Subnet]);

        args.Add(spec.Name);

        var output = await RunPodmanAsync(args, ct);
        return new NetworkHandle(NetworkId.From(output.Trim()), spec.Name);
    }

    public async Task DeleteNetworkAsync(NetworkId networkId, CancellationToken ct)
    {
        await RunPodmanAsync(["network", "rm", "-f", networkId.ToString()], ct);
    }

    private async Task<string> RunPodmanAsync(IEnumerable<string> arguments, CancellationToken ct)
    {
        var argList = arguments.ToList();
        LogPodmanCommand(string.Join(" ", argList));
        return await runner.RunAsync("podman", argList, ct);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Running: podman {Arguments}")]
    private partial void LogPodmanCommand(string arguments);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace {WorkspaceId} provisioned with {ContainerCount} containers")]
    private partial void LogWorkspaceProvisioned(string workspaceId, int containerCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace {WorkspaceId} torn down")]
    private partial void LogWorkspaceTornDown(string workspaceId);
}
