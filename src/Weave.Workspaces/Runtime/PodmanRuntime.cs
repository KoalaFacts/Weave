using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Weave.Workspaces.Models;

namespace Weave.Workspaces.Runtime;

public sealed partial class PodmanRuntime(ILogger<PodmanRuntime> logger) : IWorkspaceRuntime
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

        return new WorkspaceEnvironment(workspaceId, network.NetworkId, containers);
    }

    public async Task TeardownAsync(string workspaceId, CancellationToken ct)
    {
        // Stop all containers in the workspace
        var output = await RunPodmanAsync(["ps", "--filter", $"name=weave-{workspaceId}", "--format", "{{.ID}}"], ct);
        var containerIds = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var id in containerIds)
        {
            await StopContainerAsync(id.Trim(), ct);
        }

        // Remove network
        await RunPodmanAsync(["network", "rm", "-f", $"weave-{workspaceId}"], ct);

        LogWorkspaceTornDown(workspaceId);
    }

    public async Task<ContainerHandle> StartContainerAsync(ContainerSpec spec, CancellationToken ct)
    {
        var args = new List<string> { "run", "-d", "--name", spec.Name };

        if (spec.NetworkId is not null)
            args.AddRange(["--network", spec.NetworkId]);

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
        var containerId = output.Trim();

        return new ContainerHandle(containerId, spec.Name, spec.Image, spec.PortMappings);
    }

    public async Task StopContainerAsync(string containerId, CancellationToken ct)
    {
        await RunPodmanAsync(["stop", containerId], ct);
        await RunPodmanAsync(["rm", "-f", containerId], ct);
    }

    public async Task<NetworkHandle> CreateNetworkAsync(NetworkSpec spec, CancellationToken ct)
    {
        var args = new List<string> { "network", "create" };

        if (spec.Subnet is not null)
            args.AddRange(["--subnet", spec.Subnet]);

        args.Add(spec.Name);

        var output = await RunPodmanAsync(args, ct);
        return new NetworkHandle(output.Trim(), spec.Name);
    }

    public async Task DeleteNetworkAsync(string networkId, CancellationToken ct)
    {
        await RunPodmanAsync(["network", "rm", "-f", networkId], ct);
    }

    private async Task<string> RunPodmanAsync(IEnumerable<string> arguments, CancellationToken ct)
    {
        var argList = arguments.ToList();
        var startInfo = new ProcessStartInfo
        {
            FileName = "podman",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in argList)
            startInfo.ArgumentList.Add(arg);

        LogPodmanCommand(string.Join(" ", argList));

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start podman process.");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Podman command failed (exit code {process.ExitCode}): {stderr}");

        return stdout;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Running: podman {Arguments}")]
    private partial void LogPodmanCommand(string arguments);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace {WorkspaceId} provisioned with {ContainerCount} containers")]
    private partial void LogWorkspaceProvisioned(string workspaceId, int containerCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace {WorkspaceId} torn down")]
    private partial void LogWorkspaceTornDown(string workspaceId);
}
