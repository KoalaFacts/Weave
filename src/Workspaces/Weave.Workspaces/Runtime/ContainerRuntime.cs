using Microsoft.Extensions.Logging;
using Weave.Shared.Ids;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Runtime;

public sealed partial class ContainerRuntime(
    ICommandRunner runner,
    ContainerRuntimeOptions options,
    ILogger logger) : IWorkspaceRuntime
{
    private readonly string _engine = NormalizeEngine(options.Engine);

    public string RuntimeName => _engine;

    public async Task<WorkspaceEnvironment> ProvisionAsync(WorkspaceManifest manifest, CancellationToken ct)
    {
        var workspaceId = manifest.Name;

        var networkName = manifest.Workspace.Network?.Name?.Replace("{workspace}", workspaceId)
            ?? $"weave-{workspaceId}";
        var network = await CreateNetworkAsync(new NetworkSpec
        {
            Name = networkName,
            Subnet = manifest.Workspace.Network?.Subnet
        }, ct);

        var containers = new List<ContainerHandle>();
        foreach (var (toolName, tool) in manifest.Tools.Where(static kvp => kvp.Value.Type is "mcp" && kvp.Value.Mcp is not null))
        {
            var container = await StartContainerAsync(new ContainerSpec
            {
                Name = $"weave-{workspaceId}-{toolName}",
                Image = tool.Mcp!.Server,
                Environment = tool.Mcp.Env,
                NetworkId = network.NetworkId,
                Command = tool.Mcp.Args
            }, ct);

            containers.Add(container);
        }

        LogWorkspaceProvisioned(logger, workspaceId, containers.Count);

        return new WorkspaceEnvironment(WorkspaceId.From(workspaceId), network.NetworkId, containers);
    }

    public async Task TeardownAsync(WorkspaceId workspaceId, CancellationToken ct)
    {
        var output = await RunContainerCliAsync(["ps", "--filter", $"name=weave-{workspaceId}", "--format", "{{.ID}}"], ct);
        var containerIds = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var id in containerIds)
            await StopContainerAsync(ContainerId.From(id.Trim()), ct);

        await DeleteNetworkAsync(NetworkId.From($"weave-{workspaceId}"), ct);

        LogWorkspaceTornDown(logger, workspaceId.ToString());
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

        var output = await RunContainerCliAsync(args, ct);
        var containerId = ContainerId.From(output.Trim());

        return new ContainerHandle(containerId, spec.Name, spec.Image, spec.PortMappings);
    }

    public async Task StopContainerAsync(ContainerId containerId, CancellationToken ct)
    {
        await RunContainerCliAsync(["stop", containerId.ToString()], ct);
        await RunContainerCliAsync(["rm", "-f", containerId.ToString()], ct);
    }

    public async Task<NetworkHandle> CreateNetworkAsync(NetworkSpec spec, CancellationToken ct)
    {
        var args = new List<string> { "network", "create" };

        if (spec.Subnet is not null)
            args.AddRange(["--subnet", spec.Subnet]);

        args.Add(spec.Name);

        var output = await RunContainerCliAsync(args, ct);
        return new NetworkHandle(NetworkId.From(output.Trim()), spec.Name);
    }

    public async Task DeleteNetworkAsync(NetworkId networkId, CancellationToken ct)
    {
        var args = _engine is ContainerRuntimeOptions.DockerEngine
            ? new List<string> { "network", "rm", networkId.ToString() }
            : ["network", "rm", "-f", networkId.ToString()];

        await RunContainerCliAsync(args, ct);
    }

    private async Task<string> RunContainerCliAsync(IEnumerable<string> arguments, CancellationToken ct)
    {
        var argList = arguments.ToList();
        LogContainerCommand(logger, _engine, string.Join(" ", argList));
        return await runner.RunAsync(_engine, argList, ct);
    }

    private static string NormalizeEngine(string engine)
    {
        var normalized = engine.Trim().ToLowerInvariant();
        return normalized switch
        {
            ContainerRuntimeOptions.PodmanEngine or ContainerRuntimeOptions.DockerEngine => normalized,
            _ => throw new InvalidOperationException(
                $"Unsupported container runtime '{engine}'. Supported values are '{ContainerRuntimeOptions.PodmanEngine}' and '{ContainerRuntimeOptions.DockerEngine}'.")
        };
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Running: {Command} {Arguments}")]
    private static partial void LogContainerCommand(ILogger logger, string command, string arguments);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace {WorkspaceId} provisioned with {ContainerCount} containers")]
    private static partial void LogWorkspaceProvisioned(ILogger logger, string workspaceId, int containerCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace {WorkspaceId} torn down")]
    private static partial void LogWorkspaceTornDown(ILogger logger, string workspaceId);
}
