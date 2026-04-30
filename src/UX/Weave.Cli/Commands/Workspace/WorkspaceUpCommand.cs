using System.CommandLine;
using System.Globalization;
using Spectre.Console;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

internal static class WorkspaceUpCommand
{
    public static Command Create()
    {
        var nameArg = new Argument<string?>("name")
        {
            Description = "Workspace name",
            Arity = ArgumentArity.ZeroOrOne
        };
        nameArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);
        var targetOption = new Option<string>("--target")
        {
            Description = "Deployment target",
            DefaultValueFactory = _ => "local"
        };
        targetOption.CompletionSources.Add(CliCompletions.CompleteDeployTargets);

        var cmd = new Command("up", "Start a workspace") { nameArg, targetOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg);
            var target = parseResult.GetValue(targetOption)!;

            // Guided mode: pick a workspace when none specified
            if (string.IsNullOrWhiteSpace(name))
            {
                var manifestHere = ManifestResolver.Resolve(null);
                if (manifestHere is not null)
                {
                    name = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(manifestHere)));
                }
                else
                {
                    var all = WorkspaceRegistry.GetAll();
                    if (all.Count == 0)
                    {
                        CliTheme.WriteError("No workspaces found. Create one first:");
                        CliTheme.WriteMuted("  weave workspace new");
                        return 1;
                    }

                    name = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Which workspace would you like to start?")
                            .Styled()
                            .AddChoices(all.Keys));
                }
            }

            var manifestPath = ManifestResolver.Resolve(name);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{name}'.");
                return 1;
            }

            CliTheme.WriteInfo($"Starting workspace from {manifestPath} (target: {target})...");

            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var parser = new ManifestParser();
            var manifest = WorkspaceApiClient.PrepareManifest(
                parser.Parse(json),
                Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Directory.GetCurrentDirectory());

            try
            {
                using var client = new WorkspaceApiClient();

                if (!await client.IsReachableAsync(cancellationToken))
                {
                    CliTheme.WriteInfo("Server not running — starting automatically...");
                    var started = await WorkspaceSiloStarter.AutoStartServeAsync(cancellationToken);
                    if (!started)
                    {
                        CliTheme.WriteError("Could not start the Weave server.");
                        CliTheme.WriteMuted("  Start it manually with: weave serve");
                        return 1;
                    }

                    CliTheme.WriteSuccess("Server ready.");
                }

                var response = await client.StartWorkspaceAsync(manifest, cancellationToken);
                var statePath = WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);
                Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
                await File.WriteAllTextAsync(statePath, response.WorkspaceId, cancellationToken);

                CliTheme.WriteKeyValue("Workspace", manifest.Name);
                CliTheme.WriteKeyValue("Workspace ID", response.WorkspaceId);
                CliTheme.WriteKeyValue("Status", response.Status);
                CliTheme.WriteKeyValue("Agents", manifest.Agents.Count.ToString(CultureInfo.InvariantCulture));
                CliTheme.WriteKeyValue("Tools", manifest.Tools.Count.ToString(CultureInfo.InvariantCulture));
                CliTheme.WriteSuccess("Workspace started successfully.");
            }
            catch (Exception ex)
            {
                CliTheme.WriteError($"Failed to start workspace: {ex.Message}");
                return 1;
            }

            return 0;
        });

        return cmd;
    }
}
