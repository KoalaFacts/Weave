namespace Weave.Cli.Commands;

internal sealed class DataExportCliCommand(
    WorkspaceDataExporter? exporter = null,
    DataExportWorkspaceSelector? selector = null,
    WorkspaceManifestFile? manifests = null) : ICliCommand<DataExportOptions>
{
    private readonly WorkspaceDataExporter _exporter = exporter ?? new WorkspaceDataExporter();
    private readonly DataExportWorkspaceSelector _selector = selector ?? new DataExportWorkspaceSelector();
    private readonly WorkspaceManifestFile _manifests = manifests ?? new WorkspaceManifestFile();

    public string Name => "export";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Export a complete workspace snapshot to a portable JSON file";

    public async Task<int> ExecuteAsync(DataExportOptions options, CancellationToken ct)
    {
        var workspace = _selector.SelectWorkspace(options.Workspace);
        var manifestPath = _selector.ResolveManifestPath(workspace);
        if (manifestPath is null)
        {
            WorkspacePrompt.WriteManifestNotFound(workspace);
            return 1;
        }

        if (workspace is null)
        {
            var manifest = await _manifests.ReadAsync(manifestPath, ct);
            workspace = manifest.Name;
        }

        var outputPath = options.Output ?? $"{workspace}-export.json";

        using var client = new WorkspaceApiClient();
        using var marketplaceClient = new MarketplaceApiClient();
        if (!await client.IsReachableAsync(ct))
        {
            CliTheme.WriteError("Weave server is not running. Start it with 'weave run'.");
            return 1;
        }

        CliTheme.WriteInfo($"Exporting workspace '{workspace}'...");

        var export = await _exporter.BuildExportAsync(workspace, manifestPath, client, marketplaceClient, ct);
        await _exporter.WriteAsync(export, outputPath, ct);

        Spectre.Console.AnsiConsole.WriteLine();
        CliTheme.WriteSuccess($"Exported to {Path.GetFullPath(outputPath)}");
        CliTheme.WriteMuted($"  File size: {new FileInfo(outputPath).Length / 1024} KB");
        CliTheme.WriteMuted("  Import on another machine with: weave data import " + Path.GetFileName(outputPath));

        return 0;
    }
}
