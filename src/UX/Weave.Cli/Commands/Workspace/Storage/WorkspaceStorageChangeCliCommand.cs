using Spectre.Console;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceStorageChangeCliCommand : ICliCommand<WorkspaceStorageChangeOptions>
{
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

        var parser = new ManifestParser();
        var json = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = parser.Parse(json);
        var currentStorage = manifest.Workspace.Storage;

        CliTheme.WriteSection($"Change Storage — {options.Workspace}");
        if (currentStorage is not null)
            CliTheme.WriteKeyValue("Current", $"{currentStorage.Backend} ({currentStorage.Isolation.ToString().ToLowerInvariant()})");
        else
            CliTheme.WriteKeyValue("Current", "(global default)");
        AnsiConsole.WriteLine();

        var backend = options.Backend;
        if (string.IsNullOrWhiteSpace(backend))
        {
            backend = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Backend:")
                    .Styled()
                    .AddChoices(WorkspaceStorageCommands.SupportedBackends));
        }

        if (!WorkspaceStorageCommands.SupportedBackends.Contains(backend, StringComparer.OrdinalIgnoreCase))
        {
            CliTheme.WriteError($"Unknown backend '{backend}'. Supported: {string.Join(", ", WorkspaceStorageCommands.SupportedBackends)}");
            return 1;
        }

        var database = options.Database ?? "weave";
        var isolation = ResolveIsolation(options.Isolation);
        var connectionStr = PromptConnectionString(options, manifestPath, backend, database);

        if (backend is "postgresql" or "sqlserver" or "sqlite" && !string.IsNullOrWhiteSpace(connectionStr))
        {
            var dbExists = await WorkspaceStorageCommands.CheckDatabaseExistsAsync(backend, connectionStr, database, ct);
            if (dbExists)
            {
                var outcome = PromptDatabaseConflict(backend, connectionStr, database);
                if (outcome.Abort)
                    return 0;

                database = outcome.Database;
                connectionStr = outcome.ConnectionString;
            }
        }

        var schema = options.Schema;
        if (backend is "postgresql" or "sqlserver" && isolation == StorageIsolation.Schema && string.IsNullOrWhiteSpace(schema))
        {
            schema = AnsiConsole.Prompt(
                new TextPrompt<string>("Schema name:")
                    .Styled()
                    .DefaultValue(options.Workspace));
        }

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
        await File.WriteAllTextAsync(manifestPath, parser.Serialize(manifest), ct);

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

    private static StorageIsolation ResolveIsolation(string? isolationStr)
    {
        if (!string.IsNullOrWhiteSpace(isolationStr))
            return isolationStr.Equals("schema", StringComparison.OrdinalIgnoreCase)
                ? StorageIsolation.Schema
                : StorageIsolation.Database;

        return StorageIsolation.Database;
    }

    private static string? PromptConnectionString(WorkspaceStorageChangeOptions options, string manifestPath, string backend, string database)
    {
        if (backend is "memory" || !string.IsNullOrWhiteSpace(options.ConnectionString))
            return options.ConnectionString;

        var defaultConn = backend switch
        {
            "sqlite" => $"Data Source={Path.Combine(Path.GetDirectoryName(manifestPath)!, ".weave", "workspace.db")}",
            "postgresql" => $"Host=localhost;Database={database};Username=weave;Password=weave",
            "sqlserver" => $"Server=localhost;Database={database};Trusted_Connection=true;TrustServerCertificate=true",
            "redis" => "localhost:6379",
            _ => ""
        };

        return AnsiConsole.Prompt(
            new TextPrompt<string>("Connection string:")
                .Styled()
                .DefaultValue(defaultConn));
    }

    private static (bool Abort, string Database, string ConnectionString) PromptDatabaseConflict(string backend, string connectionString, string database)
    {
        CliTheme.WriteWarning($"Database '{database}' already exists.");
        var action = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("What would you like to do?")
                .Styled()
                .AddChoices(
                    "stop     — abort, do not change storage",
                    "override — use the existing database (data may conflict)",
                    "rename   — choose a different database name"));

        if (action.StartsWith("stop", StringComparison.OrdinalIgnoreCase))
        {
            CliTheme.WriteInfo("Aborted. No changes made.");
            return (true, database, connectionString);
        }

        if (!action.StartsWith("rename", StringComparison.OrdinalIgnoreCase))
            return (false, database, connectionString);

        var newDatabase = AnsiConsole.Prompt(new TextPrompt<string>("New database name:").Styled());
        return (false, newDatabase, WorkspaceStorageCommands.ReplaceDatabaseInConnectionString(backend, connectionString, newDatabase));
    }
}
