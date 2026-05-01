using Spectre.Console;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for CLI testability.")]
internal sealed class WorkspaceStorageChangePrompt(WorkspaceStorageBackendService? storage = null)
{
    private readonly WorkspaceStorageBackendService _storage = storage ?? new WorkspaceStorageBackendService();

    public string SelectBackend(string? backend) => !string.IsNullOrWhiteSpace(backend)
        ? backend
        : AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Backend:")
                .Styled()
                .AddChoices(_storage.SupportedBackends));

    public StorageIsolation ResolveIsolation(string? isolation) => !string.IsNullOrWhiteSpace(isolation) &&
        isolation.Equals("schema", StringComparison.OrdinalIgnoreCase)
            ? StorageIsolation.Schema
            : StorageIsolation.Database;

    public string? PromptConnectionString(WorkspaceStorageChangeOptions options, string manifestPath, string backend, string database)
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

    public string? PromptSchema(string? schema, string workspace, string backend, StorageIsolation isolation)
    {
        if (backend is not ("postgresql" or "sqlserver") || isolation != StorageIsolation.Schema || !string.IsNullOrWhiteSpace(schema))
            return schema;

        return AnsiConsole.Prompt(
            new TextPrompt<string>("Schema name:")
                .Styled()
                .DefaultValue(workspace));
    }

    public WorkspaceStorageConflictResolution PromptDatabaseConflict(string backend, string connectionString, string database)
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
            return new WorkspaceStorageConflictResolution(true, database, connectionString);
        }

        if (!action.StartsWith("rename", StringComparison.OrdinalIgnoreCase))
            return new WorkspaceStorageConflictResolution(false, database, connectionString);

        var newDatabase = AnsiConsole.Prompt(new TextPrompt<string>("New database name:").Styled());
        return new WorkspaceStorageConflictResolution(
            false,
            newDatabase,
            _storage.ReplaceDatabaseInConnectionString(backend, connectionString, newDatabase));
    }
}
