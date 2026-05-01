using Spectre.Console;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceStorageChangeCliCommand(
    WorkspaceStorageBackendService? storage = null,
    WorkspaceStorageChangePrompt? prompt = null,
    WorkspaceManifestFile? manifests = null) : ICliCommand<WorkspaceStorageChangeOptions>
{
    private readonly WorkspaceStorageBackendService _storage = storage ?? new WorkspaceStorageBackendService();
    private readonly WorkspaceStorageChangePrompt _prompt = prompt ?? new WorkspaceStorageChangePrompt(storage);
    private readonly WorkspaceManifestFile _manifests = manifests ?? new WorkspaceManifestFile();

    public string Name => "change";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Change workspace storage backend";

    public async Task<int> ExecuteAsync(WorkspaceStorageChangeOptions options, CancellationToken ct)
    {
        var manifestPath = ManifestResolver.Resolve(options.Workspace);
        if (manifestPath is null)
        {
            CliTheme.WriteError($"No workspace.json found for '{options.Workspace}'.");
            return 1;
        }

        var manifest = await _manifests.ReadAsync(manifestPath, ct);
        var currentStorage = manifest.Workspace.Storage;

        CliTheme.WriteSection($"Change Storage — {options.Workspace}");
        if (currentStorage is not null)
            CliTheme.WriteKeyValue("Current", $"{currentStorage.Backend} ({currentStorage.Isolation.ToString().ToLowerInvariant()})");
        else
            CliTheme.WriteKeyValue("Current", "(global default)");
        AnsiConsole.WriteLine();

        var backend = _prompt.SelectBackend(options.Backend);

        if (!_storage.SupportedBackends.Contains(backend, StringComparer.OrdinalIgnoreCase))
        {
            CliTheme.WriteError($"Unknown backend '{backend}'. Supported: {string.Join(", ", _storage.SupportedBackends)}");
            return 1;
        }

        var database = options.Database ?? "weave";
        var isolation = _prompt.ResolveIsolation(options.Isolation);
        var connectionStr = _prompt.PromptConnectionString(options, manifestPath, backend, database);

        if (backend is "postgresql" or "sqlserver" or "sqlite" && !string.IsNullOrWhiteSpace(connectionStr))
        {
            var dbExists = await _storage.CheckDatabaseExistsAsync(backend, connectionStr, database, ct);
            if (dbExists)
            {
                var outcome = _prompt.PromptDatabaseConflict(backend, connectionStr, database);
                if (outcome.Abort)
                    return 0;

                database = outcome.Database;
                connectionStr = outcome.ConnectionString;
            }
        }

        var schema = _prompt.PromptSchema(options.Schema, options.Workspace, backend, isolation);

        var newStorage = backend == "memory"
            ? null
            : new StorageConfig
            {
                Backend = backend,
                ConnectionString = connectionStr,
                Schema = isolation == StorageIsolation.Schema ? schema : null,
                Database = database,
                Isolation = isolation
            };

        manifest = manifest with { Workspace = manifest.Workspace with { Storage = newStorage } };
        await _manifests.WriteAsync(manifestPath, manifest, ct);

        AnsiConsole.WriteLine();
        if (newStorage is null)
        {
            CliTheme.WriteSuccess($"Workspace '{options.Workspace}' reset to global default storage.");
        }
        else
        {
            CliTheme.WriteSuccess($"Workspace '{options.Workspace}' storage set to {backend}.");
            CliTheme.WriteKeyValue("Database", database);
            CliTheme.WriteKeyValue("Isolation", isolation.ToString().ToLowerInvariant());
            if (isolation == StorageIsolation.Schema && !string.IsNullOrWhiteSpace(schema))
                CliTheme.WriteKeyValue("Schema", schema);
        }

        CliTheme.WriteMuted("  Start with: weave run " + options.Workspace);
        return 0;
    }
}
