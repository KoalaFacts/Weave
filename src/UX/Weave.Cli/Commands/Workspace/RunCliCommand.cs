using System.Diagnostics;
using System.Globalization;
using Spectre.Console;

namespace Weave.Cli.Commands;

internal sealed class RunCliCommand(
    SiloProcessService? silo = null,
    RunWorkspaceSelector? workspaceSelector = null,
    WorkspaceManifestFile? manifests = null) : ICliCommand<RunOptions>
{
    private readonly SiloProcessService _silo = silo ?? new SiloProcessService();
    private readonly RunWorkspaceSelector _workspaceSelector = workspaceSelector ?? new RunWorkspaceSelector();
    private readonly WorkspaceManifestFile _manifests = manifests ?? new WorkspaceManifestFile();

    public string Name => "run";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Start the server and workspace in one command";

    public async Task<int> ExecuteAsync(RunOptions options, CancellationToken ct)
    {
        var port = options.Port;
        var selection = _workspaceSelector.Select(options.Name);
        if (!selection.ShouldRun)
            return selection.ExitCode;

        var manifestPath = selection.ManifestPath!;

        CliTheme.WriteBanner();

        var manifest = await _manifests.ReadPreparedAsync(manifestPath, ct);

        CliTheme.WriteKeyValue("Workspace", manifest.Name);
        CliTheme.WriteKeyValue("Agents", manifest.Agents.Count.ToString(CultureInfo.InvariantCulture));
        CliTheme.WriteKeyValue("Tools", manifest.Tools.Count.ToString(CultureInfo.InvariantCulture));
        if (manifest.Channels.Count > 0)
            CliTheme.WriteKeyValue("Channels", manifest.Channels.Count.ToString(CultureInfo.InvariantCulture));
        AnsiConsole.WriteLine();

        var serverAlreadyRunning = await _silo.IsReachableAsync(port, ct);
        Process? siloProcess = null;

        if (!serverAlreadyRunning)
        {
            CliTheme.WriteInfo($"Starting server on port {port}...");

            var siloPath = WorkspaceSiloStarter.ResolveSiloPath();
            if (siloPath is null)
            {
                CliTheme.WriteError("Could not locate the Weave silo.");
                CliTheme.WriteMuted("  Run `weave init` to configure, or set WEAVE_SILO_PATH.");
                return 1;
            }

            siloProcess = _silo.StartSilo(siloPath, port, manifest.Workspace.Storage);
            if (siloProcess is null)
            {
                CliTheme.WriteError("Failed to start server.");
                return 1;
            }

            var ready = await _silo.WaitForReadyAsync(port, ct);
            if (!ready)
            {
                CliTheme.WriteError("Server did not become ready in time.");
                _silo.TryKill(siloProcess);
                return 1;
            }

            CliTheme.WriteSuccess("Server ready.");
        }
        else
        {
            CliTheme.WriteInfo("Server already running.");
        }

        try
        {
            using var client = new WorkspaceApiClient($"http://localhost:{port}");
            var response = await client.StartWorkspaceAsync(manifest, ct);

            var statePath = WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            await File.WriteAllTextAsync(statePath, response.WorkspaceId, ct);

            AnsiConsole.WriteLine();
            CliTheme.WriteSuccess($"Workspace \"{manifest.Name}\" is running.");
            CliTheme.WriteKeyValue("Workspace ID", response.WorkspaceId);
            CliTheme.WriteKeyValue("API", $"http://localhost:{port}");
            CliTheme.WriteKeyValue("Dashboard", $"http://localhost:{port + 1}");
            AnsiConsole.WriteLine();
            CliTheme.WriteMuted("  Press Ctrl+C to stop.");

            if (siloProcess is not null)
                await siloProcess.WaitForExitAsync(ct);
            else
                await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.WriteLine();
            CliTheme.WriteInfo("Shutting down...");
        }
        catch (Exception ex)
        {
            CliTheme.WriteError($"Failed to start workspace: {ex.Message}");
            _silo.TryKill(siloProcess);
            return 1;
        }
        finally
        {
            _silo.TryKill(siloProcess);
        }

        return 0;
    }
}
