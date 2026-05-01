namespace Weave.Cli.Commands;

internal sealed class WorkspaceStorageShowCliCommand(
    WorkspaceStorageBackendService? storage = null,
    WorkspaceManifestFile? manifests = null) : ICliCommand<WorkspaceNameOptions>
{
    private readonly WorkspaceStorageBackendService _storage = storage ?? new WorkspaceStorageBackendService();
    private readonly WorkspaceManifestFile _manifests = manifests ?? new WorkspaceManifestFile();

    public string Name => "show";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Show workspace storage configuration";

    public async Task<int> ExecuteAsync(WorkspaceNameOptions options, CancellationToken ct)
    {
        var name = WorkspacePrompt.SelectName(options.Name, "Which workspace would you like to inspect?");
        var manifestPath = ManifestResolver.Resolve(name);
        if (manifestPath is null)
        {
            CliTheme.WriteError(name is null
                ? "No workspace.json found. Create one first with: weave workspace new"
                : $"No workspace.json found for '{name}'.");
            return 1;
        }

        var manifest = await _manifests.ReadAsync(manifestPath, ct);

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
                CliTheme.WriteKeyValue("Connection", _storage.MaskPassword(storage.ConnectionString));
        }

        return 0;
    }
}
