using System.Diagnostics;
using System.Globalization;
using Spectre.Console;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

internal sealed class RunCliCommand : ICliCommand<RunOptions>
{
    public string Name => "run";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Start the server and workspace in one command";

    public async Task<int> ExecuteAsync(RunOptions options, CancellationToken ct)
    {
        var name = options.Name;
        var port = options.Port;

        var manifestPath = ManifestResolver.Resolve(name);

        // Guided mode: no workspace found — help the user pick or create one
        if (manifestPath is null)
        {
            CliTheme.WriteBanner();

            var existing = WorkspaceRegistry.GetAll();
            if (existing.Count > 0)
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
                    return 0;
                }

                name = picked;
                manifestPath = ManifestResolver.Resolve(name);
            }
            else
            {
                CliTheme.WriteInfo("No workspaces found. Let's create one.");
                AnsiConsole.WriteLine();

                var wsName = AnsiConsole.Prompt(
                    new TextPrompt<string>("Workspace name:")
                        .Styled()
                        .DefaultValue("my-workspace"));

                var preset = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Choose a preset:")
                        .Styled()
                        .AddChoices([.. WorkspacePresets.All.Keys]));

                CliTheme.WriteMuted($"  Creating workspace '{wsName}' with preset '{preset}'...");
                CliTheme.WriteMuted($"  Run: weave workspace new {wsName} --preset {preset}");
                CliTheme.WriteMuted($"  Then: weave run {wsName}");
                return 0;
            }

            if (manifestPath is null)
            {
                CliTheme.WriteError($"Workspace '{name}' exists but has no workspace.json.");
                return 1;
            }
        }

        CliTheme.WriteBanner();

        var json = await File.ReadAllTextAsync(manifestPath, ct);
        var parser = new ManifestParser();
        var manifest = WorkspaceApiClient.PrepareManifest(
            parser.Parse(json),
            Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Directory.GetCurrentDirectory());

        CliTheme.WriteKeyValue("Workspace", manifest.Name);
        CliTheme.WriteKeyValue("Agents", manifest.Agents.Count.ToString(CultureInfo.InvariantCulture));
        CliTheme.WriteKeyValue("Tools", manifest.Tools.Count.ToString(CultureInfo.InvariantCulture));
        if (manifest.Channels.Count > 0)
            CliTheme.WriteKeyValue("Channels", manifest.Channels.Count.ToString(CultureInfo.InvariantCulture));
        AnsiConsole.WriteLine();

        var serverAlreadyRunning = await RunCommand.IsReachableAsync(port, ct);
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

            siloProcess = RunCommand.StartSilo(siloPath, port, manifest.Workspace.Storage);
            if (siloProcess is null)
            {
                CliTheme.WriteError("Failed to start server.");
                return 1;
            }

            var ready = await RunCommand.WaitForReadyAsync(port, ct);
            if (!ready)
            {
                CliTheme.WriteError("Server did not become ready in time.");
                RunCommand.TryKill(siloProcess);
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
            RunCommand.TryKill(siloProcess);
            return 1;
        }
        finally
        {
            RunCommand.TryKill(siloProcess);
        }

        return 0;
    }
}
