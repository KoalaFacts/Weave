using System.Diagnostics;
using System.Globalization;
using Spectre.Console;
using Weave.Actions.Context;
using Weave.Actions.Workspace;

namespace Weave.Cli.Commands;

internal sealed class RunCliCommand(
    IManifestResolver manifestResolver,
    IWorkspaceRegistry registry,
    ISiloLauncher siloLauncher,
    SiloProcessService siloProcessService) : ICliCommand<RunOptions>
{

    public string Name => "run";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Start the server and workspace in one command";

    public async Task<int> ExecuteAsync(RunOptions options, CancellationToken ct)
    {
        var port = options.Port;
        var selection = SelectWorkspace(options.Name);
        if (!selection.ShouldRun)
            return selection.ExitCode;

        var manifestPath = selection.ManifestPath!;

        CliTheme.WriteBanner();

        var manifest = await WorkspaceManifestFile.ReadPreparedAsync(manifestPath, ct);

        CliTheme.WriteKeyValue("Workspace", manifest.Name);
        CliTheme.WriteKeyValue("Agents", manifest.Agents.Count.ToString(CultureInfo.InvariantCulture));
        CliTheme.WriteKeyValue("Tools", manifest.Tools.Count.ToString(CultureInfo.InvariantCulture));
        if (manifest.Channels.Count > 0)
            CliTheme.WriteKeyValue("Channels", manifest.Channels.Count.ToString(CultureInfo.InvariantCulture));
        AnsiConsole.WriteLine();

        var serverAlreadyRunning = await SiloProcessService.IsReachableAsync(port, ct);
        Process? siloProc = null;

        if (!serverAlreadyRunning)
        {
            CliTheme.WriteInfo($"Starting server on port {port}...");

            var siloPath = siloLauncher.ResolveSiloPath();
            if (siloPath is null)
            {
                CliTheme.WriteError("Could not locate the Weave silo.");
                CliTheme.WriteMuted("  Run `weave init` to configure, or set WEAVE_SILO_PATH.");
                return 1;
            }

            siloProc = siloProcessService.StartSilo(siloPath, port, manifest.Workspace.Storage);
            if (siloProc is null)
            {
                CliTheme.WriteError("Failed to start server.");
                return 1;
            }

            var ready = await SiloProcessService.WaitForReadyAsync(port, ct);
            if (!ready)
            {
                CliTheme.WriteError("Server did not become ready in time.");
                SiloProcessService.TryKill(siloProc);
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
            // RunCliCommand picks a port at runtime (--port arg) so it can't
            // share the DI'd typed HttpClient (which is configured at Build()
            // with the default silo URL). One-shot run-and-block lifetime
            // makes inline HttpClient construction fine.
            using var httpClient = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}", UriKind.Absolute) };
            var startAction = new StartWorkspaceAction(httpClient);

            var result = await startAction.ExecuteAsync(new StartWorkspaceInput(manifest), ct);
            if (!result.IsSuccess)
            {
                if (result.Failure.Reason == ActionFailureReason.Cancelled)
                {
                    AnsiConsole.WriteLine();
                    CliTheme.WriteInfo("Shutting down...");
                    return 0;
                }

                CliTheme.WriteError($"Failed to start workspace: {result.Failure.Message}");
                SiloProcessService.TryKill(siloProc);
                return 1;
            }

            var workspace = result.Value.Workspace;
            var statePath = WorkspaceManifestPaths.GetStatePath(manifestPath);
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            await File.WriteAllTextAsync(statePath, workspace.WorkspaceId, ct);

            AnsiConsole.WriteLine();
            CliTheme.WriteSuccess($"Workspace \"{manifest.Name}\" is running.");
            CliTheme.WriteKeyValue("Workspace ID", workspace.WorkspaceId);
            CliTheme.WriteKeyValue("API", $"http://localhost:{port}");
            CliTheme.WriteKeyValue("Dashboard", $"http://localhost:{port + 1}");
            AnsiConsole.WriteLine();
            CliTheme.WriteMuted("  Press Ctrl+C to stop.");

            if (siloProc is not null)
                await siloProc.WaitForExitAsync(ct);
            else
                await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.WriteLine();
            CliTheme.WriteInfo("Shutting down...");
        }
        finally
        {
            SiloProcessService.TryKill(siloProc);
        }

        return 0;
    }

    private RunWorkspaceSelection SelectWorkspace(string? name)
    {
        var manifestPath = manifestResolver.Resolve(name);
        if (manifestPath is not null)
            return RunWorkspaceSelection.Run(name, manifestPath);

        CliTheme.WriteBanner();
        var existing = registry.GetAll();
        if (existing.Count > 0)
            return SelectExistingWorkspace(name, existing);

        SuggestNewWorkspace();
        return RunWorkspaceSelection.Stop(0);
    }

    private RunWorkspaceSelection SelectExistingWorkspace(string? name, IReadOnlyDictionary<string, string> existing)
    {
        CliTheme.WriteInfo(name is null
            ? "No workspace.json found in the current directory."
            : $"Workspace '{name}' not found.");
        AnsiConsole.WriteLine();

        var choices = existing.Select(w => w.Key).ToList();
        choices.Add("Create a new workspace");

        var picked = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Which workspace would you like to run?")
                .Styled()
                .AddChoices(choices));

        if (picked == "Create a new workspace")
        {
            CliTheme.WriteMuted("  Run: weave workspace new <name>");
            return RunWorkspaceSelection.Stop(0);
        }

        var manifestPath = manifestResolver.Resolve(picked);
        if (manifestPath is not null)
            return RunWorkspaceSelection.Run(picked, manifestPath);

        CliTheme.WriteError($"Workspace '{picked}' exists but has no workspace.json.");
        return RunWorkspaceSelection.Stop(1);
    }

    private static void SuggestNewWorkspace()
    {
        CliTheme.WriteInfo("No workspaces found. Let's create one.");
        AnsiConsole.WriteLine();

        var name = AnsiConsole.Prompt(
            new TextPrompt<string>("Workspace name:")
                .Styled()
                .DefaultValue("my-workspace"));

        var preset = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Choose a preset:")
                .Styled()
                .AddChoices([.. WorkspacePresets.All.Keys]));

        CliTheme.WriteMuted($"  Creating workspace '{name}' with preset '{preset}'...");
        CliTheme.WriteMuted($"  Run: weave workspace new {name} --preset {preset}");
        CliTheme.WriteMuted($"  Then: weave run {name}");
    }
}
