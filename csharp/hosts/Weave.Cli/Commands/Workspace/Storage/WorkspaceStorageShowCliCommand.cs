namespace Weave.Cli.Commands;

internal sealed class WorkspaceStorageShowCliCommand(
    IManifestResolver manifestResolver,
    WorkspacePrompt workspacePrompt,
    WorkspaceStorageBackendService? storage = null) : ICliCommand<WorkspaceNameOptions>
{
    private readonly WorkspaceStorageBackendService _storage = storage ?? new WorkspaceStorageBackendService();

    public string Name => "show";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Show workspace storage configuration";

    public async Task<int> ExecuteAsync(WorkspaceNameOptions options, CancellationToken ct)
    {
        var name = workspacePrompt.SelectName(options.Name, "Which workspace would you like to inspect?");
        var manifestPath = manifestResolver.Resolve(name);
        if (manifestPath is null)
        {
            WorkspacePrompt.WriteManifestNotFound(name);
            return 1;
        }

        var manifest = await WorkspaceManifestFile.ReadAsync(manifestPath, ct);

        CliTheme.WriteSection($"Storage — {manifest.Name}");

        var storage = manifest.Workspace.Storage;
        if (storage is null)
        {
            CliTheme.WriteKeyValue("Backend", "(uses global default)");
            CliTheme.WriteMuted("  Check global: weave storage show");
        }
        else
        {
            CliTheme.WriteKeyValue("Backend", storage.Backend);
            CliTheme.WriteKeyValue("Isolation", storage.Isolation.ToString().ToLowerInvariant());

            if (!string.IsNullOrWhiteSpace(storage.Schema))
                CliTheme.WriteKeyValue("Schema", storage.Schema);
            if (!string.IsNullOrWhiteSpace(storage.Database))
                CliTheme.WriteKeyValue("Database", storage.Database);

            if (!string.IsNullOrWhiteSpace(storage.ConnectionString))
                CliTheme.WriteKeyValue("Connection", WorkspaceStorageBackendService.MaskPassword(storage.ConnectionString));
        }

        return 0;
    }
}
