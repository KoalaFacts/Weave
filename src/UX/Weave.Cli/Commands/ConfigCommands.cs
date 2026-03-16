using System.CommandLine;
using System.Globalization;
using Spectre.Console;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

internal static class WorkspaceShowCommand
{
    public static Command Create()
    {
        var nameArg = new Argument<string>("name") { Description = "Workspace name" };
        nameArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);

        var cmd = new Command("show", "Show workspace configuration") { nameArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg)!;

            var manifestPath = ManifestResolver.Resolve(name);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{name}'.");
                return 1;
            }

            var content = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            AnsiConsole.Write(CliTheme.CreatePanel(content, "workspace.json"));
            return 0;
        });

        return cmd;
    }
}

internal static class WorkspaceValidateCommand
{
    public static Command Create()
    {
        var nameArg = new Argument<string>("name") { Description = "Workspace name" };
        nameArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);

        var cmd = new Command("validate", "Validate workspace configuration") { nameArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg)!;

            var manifestPath = ManifestResolver.Resolve(name);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{name}'.");
                return 1;
            }

            try
            {
                var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                var parser = new ManifestParser();
                var manifest = parser.Parse(json);
                var errors = parser.Validate(manifest);

                if (errors.Count > 0)
                {
                    CliTheme.WriteError("Configuration invalid:");
                    foreach (var error in errors)
                        CliTheme.WriteMuted($"  - {error}");

                    return 1;
                }

                CliTheme.WriteSuccess("Configuration valid.");
                CliTheme.WriteKeyValue("Name", manifest.Name);
                CliTheme.WriteKeyValue("Agents", (manifest.Agents?.Count ?? 0).ToString(CultureInfo.InvariantCulture));
                CliTheme.WriteKeyValue("Tools", (manifest.Tools?.Count ?? 0).ToString(CultureInfo.InvariantCulture));
                CliTheme.WriteKeyValue("Targets", (manifest.Targets?.Count ?? 0).ToString(CultureInfo.InvariantCulture));
                return 0;
            }
            catch (Exception ex)
            {
                CliTheme.WriteError($"Configuration invalid: {ex.Message}");
                return 1;
            }
        });

        return cmd;
    }
}
