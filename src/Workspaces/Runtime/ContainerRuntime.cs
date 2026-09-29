using Microsoft.Extensions.Logging;
using Weave.Shared.Ids;
using Weave.Workspaces.Manifest;
namespace Weave.Workspaces.Runtime;

public sealed partial class ContainerRuntime(
    ICommandRunner runner,
    ContainerRuntimeOptions options,
    ILogger logger) : IWorkspaceRuntime
{
    private readonly string _engine = NormalizeEngine(options.Engine);

    public Guid InstanceId { get; } = Guid.NewGuid();
    public string RuntimeName => _engine;

    public async Task<WorkspaceEnvironment> ProvisionAsync(WorkspaceId workspaceId, WorkspaceManifest manifest, CancellationToken ct)
    {
        var networkName = manifest.Workspace.Network?.Name?.Replace("{workspace}", workspaceId.ToString())
            ?? $"weave-{workspaceId}";
        var network = await CreateNetworkAsync(new NetworkSpec
        {
            Name = networkName,
            Subnet = manifest.Workspace.Network?.Subnet
        }, ct);

        var containers = new List<ContainerHandle>();
        foreach (var (toolName, tool) in manifest.Tools.Where(static kvp => kvp.Value.Type is "mcp" && kvp.Value.Mcp is not null && !string.IsNullOrEmpty(kvp.Value.Mcp.Server)))
        {
            var container = await StartContainerAsync(new ContainerSpec
            {
                Name = $"weave-{workspaceId}-{toolName}",
                Image = tool.Mcp!.Server!,
                Environment = tool.Mcp.Env,
                NetworkId = network.NetworkId,
                Command = tool.Mcp.Args
            }, ct);

            containers.Add(container);
        }

        LogWorkspaceProvisioned(logger, workspaceId, containers.Count);

        return new WorkspaceEnvironment(workspaceId, network.NetworkId, containers);
    }

    public async Task TeardownAsync(WorkspaceId workspaceId, NetworkId? networkId,
        IReadOnlyList<ContainerId> containerIds, CancellationToken ct)
    {
        List<Exception> failures = [];
        foreach (var id in containerIds)
        {
            try
            {
                await StopContainerAsync(id, ct);
            }
            catch (Exception ex) when (IsTeardownFailure(ex))
            {
                failures.Add(ex);
            }
        }

        if (networkId is not null)
        {
            try
            {
                await DeleteNetworkAsync(networkId.Value, ct);
            }
            catch (Exception ex) when (IsTeardownFailure(ex))
            {
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
            throw new AggregateException($"Workspace {workspaceId} teardown requires reconciliation.", failures);

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
        if (_engine is ContainerRuntimeOptions.PodmanEngine)
        {
            await RunContainerCliAsync(["rm", "-f", "--ignore", containerId.ToString()], ct);
            return;
        }

        try
        {
            await RunContainerCliAsync(["rm", "-f", containerId.ToString()], ct);
        }
        catch (InvalidOperationException)
        {
            var remaining = await RunContainerCliAsync(
                ["container", "ls", "--all", "--no-trunc", "--filter", $"id={containerId}", "--format", "{{.ID}}"], ct);
            if (remaining.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(containerId.ToString(), StringComparer.Ordinal))
                throw;
        }
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
            ? new List<string> { "network", "rm", "-f", networkId.ToString() }
            : ["network", "rm", "--ignore", networkId.ToString()];

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

    private static bool IsTeardownFailure(Exception ex) =>
        ex is InvalidOperationException or TimeoutException or IOException or HttpRequestException
            or System.ComponentModel.Win32Exception;

    [LoggerMessage(Level = LogLevel.Debug, Message = "Running: {Command} {Arguments}")]
    private static partial void LogContainerCommand(ILogger logger, string command, string arguments);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace {WorkspaceId} provisioned with {ContainerCount} containers")]
    private static partial void LogWorkspaceProvisioned(ILogger logger, string workspaceId, int containerCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace {WorkspaceId} torn down")]
    private static partial void LogWorkspaceTornDown(ILogger logger, string workspaceId);
}
