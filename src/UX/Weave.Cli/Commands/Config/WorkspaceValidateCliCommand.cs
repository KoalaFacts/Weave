using System.Globalization;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceValidateCliCommand : ICliCommand<WorkspaceNameOptions>
{
    public string Name => "validate";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Validate workspace configuration";

    public async Task<int> ExecuteAsync(WorkspaceNameOptions options, CancellationToken ct)
    {
        var name = WorkspacePrompt.SelectName(options.Name, "Which workspace would you like to validate?");
        var manifestPath = ManifestResolver.Resolve(name);
        if (manifestPath is null)
        {
            WorkspacePrompt.WriteManifestNotFound(name);
            return 1;
        }

        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, ct);
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
    }
}
