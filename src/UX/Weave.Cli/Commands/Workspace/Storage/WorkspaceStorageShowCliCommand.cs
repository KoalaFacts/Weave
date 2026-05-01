using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceStorageShowCliCommand(WorkspaceStorageBackendService? storage = null) : ICliCommand<WorkspaceNameOptions>
{
    private readonly WorkspaceStorageBackendService _storage = storage ?? new WorkspaceStorageBackendService();

    public string Name => "show";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Show workspace storage configuration";

    public async Task<int> ExecuteAsync(WorkspaceNameOptions options, CancellationToken ct)
    {
        var manifestPath = ManifestResolver.Resolve(options.Name);
        if (manifestPath is null)
        {
            CliTheme.WriteError($"No workspace.json found for '{options.Name}'.");
            return 1;
        }

        var json = await File.ReadAllTextAsync(manifestPath, ct);
        var parser = new ManifestParser();
        var manifest = parser.Parse(json);

        CliTheme.WriteSection($"Storage — {options.Name}");

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
