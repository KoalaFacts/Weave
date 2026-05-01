namespace Weave.Cli.Commands;

internal sealed class DataImportCliCommand(
    WorkspaceDataImporter? importer = null,
    DataImportFileSelector? selector = null) : ICliCommand<DataImportOptions>
{
    private readonly WorkspaceDataImporter _importer = importer ?? new WorkspaceDataImporter();
    private readonly DataImportFileSelector _selector = selector ?? new DataImportFileSelector();

    public string Name => "import";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Import a workspace from an export file";

    public async Task<int> ExecuteAsync(DataImportOptions options, CancellationToken ct)
    {
        var filePath = _selector.SelectFilePath(options.FilePath);

        if (!_selector.FileExists(filePath))
        {
            CliTheme.WriteError($"File not found: {filePath}");
            return 1;
        }

        var export = await _importer.ReadAsync(filePath, ct);
        if (export is null)
        {
            CliTheme.WriteError("Invalid export file.");
            return 1;
        }

        var name = options.WorkspaceName ?? export.WorkspaceName;
        CliTheme.WriteInfo($"Importing workspace '{name}' (exported {export.ExportedAt:yyyy-MM-dd HH:mm} UTC)...");

        var basePath = await _importer.RestoreFilesAsync(export, name, ct);

        using var client = new WorkspaceApiClient();
        if (await client.IsReachableAsync(ct))
            await _importer.TryStartImportedWorkspaceAsync(client, export, name, basePath, ct);
        else
            CliTheme.WriteMuted("  Server not running — files restored, start with: weave run " + name);

        Spectre.Console.AnsiConsole.WriteLine();
        CliTheme.WriteSuccess($"Workspace '{name}' imported to {basePath}");

        return 0;
    }
}
