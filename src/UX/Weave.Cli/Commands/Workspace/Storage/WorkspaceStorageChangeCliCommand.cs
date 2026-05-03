using Spectre.Console;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceStorageChangeCliCommand(
    WorkspaceStorageBackendService? storage = null,
    WorkspaceStorageChangePrompt? prompt = null) : ICliCommand<WorkspaceStorageChangeOptions>
{
    private readonly WorkspaceStorageBackendService _storage = storage ?? new WorkspaceStorageBackendService();
    private readonly WorkspaceStorageChangePrompt _prompt = prompt ?? new WorkspaceStorageChangePrompt(storage);

    public string Name => "change";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Change workspace storage backend";

    public async Task<int> ExecuteAsync(WorkspaceStorageChangeOptions options, CancellationToken ct)
    {
        var workspace = WorkspacePrompt.SelectName(options.Workspace, "Which workspace would you like to update?");
        var manifestPath = ManifestResolver.Resolve(workspace);
        if (manifestPath is null)
        {
            WorkspacePrompt.WriteManifestNotFound(workspace);
            return 1;
        }

        var manifest = await WorkspaceManifestFile.ReadAsync(manifestPath, ct);
        var workspaceName = manifest.Name;
        var currentStorage = manifest.Workspace.Storage;

        CliTheme.WriteSection($"Change Storage — {workspaceName}");
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
        var isolation = WorkspaceStorageChangePrompt.ResolveIsolation(options.Isolation);
        var connectionStr = WorkspaceStorageChangePrompt.PromptConnectionString(options, manifestPath, backend, database);

        if (backend is "postgresql" or "sqlserver" or "sqlite" && !string.IsNullOrWhiteSpace(connectionStr))
        {
            var dbExists = await WorkspaceStorageBackendService.CheckDatabaseExistsAsync(backend, connectionStr, database, ct);
            if (dbExists)
            {
                var outcome = WorkspaceStorageChangePrompt.PromptDatabaseConflict(backend, connectionStr, database);
                if (outcome.Abort)
                    return 0;

                database = outcome.Database;
                connectionStr = outcome.ConnectionString;
            }
        }

        var schema = WorkspaceStorageChangePrompt.PromptSchema(options.Schema, workspaceName, backend, isolation);

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
        await WorkspaceManifestFile.WriteAsync(manifestPath, manifest, ct);

        AnsiConsole.WriteLine();
        if (newStorage is null)
        {
            CliTheme.WriteSuccess($"Workspace '{workspaceName}' reset to global default storage.");
        }
        else
        {
            CliTheme.WriteSuccess($"Workspace '{workspaceName}' storage set to {backend}.");
            CliTheme.WriteKeyValue("Database", database);
            CliTheme.WriteKeyValue("Isolation", isolation.ToString().ToLowerInvariant());
            if (isolation == StorageIsolation.Schema && !string.IsNullOrWhiteSpace(schema))
                CliTheme.WriteKeyValue("Schema", schema);
        }

        CliTheme.WriteMuted("  Start with: weave run " + workspaceName);
        return 0;
    }
}
