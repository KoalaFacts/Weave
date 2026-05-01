using System.Globalization;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceUpCliCommand(WorkspaceManifestFile? manifests = null) : ICliCommand<WorkspaceUpOptions>
{
    private readonly WorkspaceManifestFile _manifests = manifests ?? new WorkspaceManifestFile();

    public string Name => "up";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Start a workspace";

    public async Task<int> ExecuteAsync(WorkspaceUpOptions options, CancellationToken ct)
    {
        var name = WorkspacePrompt.SelectName(options.Name, "Which workspace would you like to start?");
        var manifestPath = ManifestResolver.Resolve(name);
        if (manifestPath is null)
        {
            CliTheme.WriteError(name is null
                ? "No workspace.json found. Create one first with: weave workspace new"
                : $"No workspace.json found for '{name}'.");
            return 1;
        }

        CliTheme.WriteInfo($"Starting workspace from {manifestPath} (target: {options.Target})...");

        var manifest = await _manifests.ReadPreparedAsync(manifestPath, ct);

        try
        {
            using var client = new WorkspaceApiClient();

            if (!await client.IsReachableAsync(ct))
            {
                CliTheme.WriteInfo("Server not running — starting automatically...");
                var started = await WorkspaceSiloStarter.AutoStartServeAsync(ct);
                if (!started)
                {
                    CliTheme.WriteError("Could not start the Weave server.");
                    CliTheme.WriteMuted("  Start it manually with: weave serve");
                    return 1;
                }

                CliTheme.WriteSuccess("Server ready.");
            }

            var response = await client.StartWorkspaceAsync(manifest, ct);
            var statePath = WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            await File.WriteAllTextAsync(statePath, response.WorkspaceId, ct);

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
    }
}
