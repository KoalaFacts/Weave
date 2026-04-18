using System.CommandLine;
using Spectre.Console;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal static class WorkspaceStorageCommands
{
    private static readonly string[] SupportedBackends = ["memory", "sqlite", "postgresql", "sqlserver", "redis"];

    public static Command Create()
    {
        var cmd = new Command("storage", "View and change workspace-level storage");

        cmd.Subcommands.Add(CreateShowCommand());
        cmd.Subcommands.Add(CreateChangeCommand());

        return cmd;
    }

    private static Command CreateShowCommand()
    {
        var workspaceArg = new Argument<string>("workspace") { Description = "Workspace name" };
        workspaceArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);

        var cmd = new Command("show", "Show workspace storage configuration") { workspaceArg };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg)!;

            var manifestPath = ManifestResolver.Resolve(workspace);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{workspace}'.");
                return 1;
            }

            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var parser = new ManifestParser();
            var manifest = parser.Parse(json);

            CliTheme.WriteSection($"Storage — {workspace}");

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
                    CliTheme.WriteKeyValue("Connection", MaskPassword(storage.ConnectionString));
            }

            return 0;
        });

        return cmd;
    }

    private static Command CreateChangeCommand()
    {
        var workspaceArg = new Argument<string>("workspace") { Description = "Workspace name" };
        workspaceArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);
        var backendArg = new Argument<string?>("backend")
        {
            Description = "Target backend (memory, sqlite, postgresql, sqlserver, redis)",
            Arity = ArgumentArity.ZeroOrOne
        };
        var connectionOption = new Option<string?>("--connection") { Description = "Connection string" };
        var schemaOption = new Option<string?>("--schema") { Description = "Database schema for isolation (e.g. workspace name)" };
        var databaseOption = new Option<string?>("--database") { Description = "Database name for database isolation mode" };
        var isolationOption = new Option<string?>("--isolation") { Description = "Isolation mode: schema (share db, separate schema) or database (separate db)" };

        var cmd = new Command("change", "Change workspace storage backend") { workspaceArg, backendArg, connectionOption, schemaOption, databaseOption, isolationOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var workspace = parseResult.GetValue(workspaceArg)!;
            var backend = parseResult.GetValue(backendArg);
            var connectionStr = parseResult.GetValue(connectionOption);
            var schema = parseResult.GetValue(schemaOption);
            var database = parseResult.GetValue(databaseOption);
            var isolationStr = parseResult.GetValue(isolationOption);

            var manifestPath = ManifestResolver.Resolve(workspace);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{workspace}'.");
                return 1;
            }

            var parser = new ManifestParser();
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            var manifest = parser.Parse(json);

            var currentStorage = manifest.Workspace.Storage;

            CliTheme.WriteSection($"Change Storage — {workspace}");
            if (currentStorage is not null)
                CliTheme.WriteKeyValue("Current", $"{currentStorage.Backend} ({currentStorage.Isolation.ToString().ToLowerInvariant()})");
            else
                CliTheme.WriteKeyValue("Current", "(global default)");
            AnsiConsole.WriteLine();

            if (string.IsNullOrWhiteSpace(backend))
            {
                backend = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Backend:")
                        .Styled()
                        .AddChoices(SupportedBackends));
            }

            if (!SupportedBackends.Contains(backend, StringComparer.OrdinalIgnoreCase))
            {
                CliTheme.WriteError($"Unknown backend '{backend}'. Supported: {string.Join(", ", SupportedBackends)}");
                return 1;
            }

            // Connection string
            if (backend is "postgresql" or "sqlserver" or "redis" or "sqlite" && string.IsNullOrWhiteSpace(connectionStr))
            {
                if (backend != "memory")
                {
                    var defaultConn = backend switch
                    {
                        "sqlite" => $"Data Source={Path.Combine(Path.GetDirectoryName(manifestPath)!, ".weave", "workspace.db")}",
                        "postgresql" => "Host=localhost;Database=weave;Username=weave;Password=weave",
                        "sqlserver" => "Server=localhost;Database=weave;Trusted_Connection=true;TrustServerCertificate=true",
                        "redis" => "localhost:6379",
                        _ => ""
                    };

                    connectionStr = AnsiConsole.Prompt(
                        new TextPrompt<string>("Connection string:")
                            .Styled()
                            .DefaultValue(defaultConn));
                }
            }

            // Isolation mode
            var isolation = StorageIsolation.Schema;
            if (backend is "postgresql" or "sqlserver")
            {
                if (string.IsNullOrWhiteSpace(isolationStr))
                {
                    isolationStr = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("Isolation mode:")
                            .Styled()
                            .AddChoices(
                                "schema   — share one database, use a schema per workspace",
                                "database — each workspace gets its own database"));

                    isolation = isolationStr.StartsWith("database", StringComparison.OrdinalIgnoreCase)
                        ? StorageIsolation.Database
                        : StorageIsolation.Schema;
                }
                else
                {
                    isolation = isolationStr.Equals("database", StringComparison.OrdinalIgnoreCase)
                        ? StorageIsolation.Database
                        : StorageIsolation.Schema;
                }

                if (isolation == StorageIsolation.Schema && string.IsNullOrWhiteSpace(schema))
                {
                    schema = AnsiConsole.Prompt(
                        new TextPrompt<string>("Schema name:")
                            .Styled()
                            .DefaultValue(workspace));
                }

                if (isolation == StorageIsolation.Database && string.IsNullOrWhiteSpace(database))
                {
                    database = AnsiConsole.Prompt(
                        new TextPrompt<string>("Database name:")
                            .Styled()
                            .DefaultValue($"weave_{workspace}"));
                }
            }

            // Build new storage config
            StorageConfig? newStorage;
            if (backend == "memory")
            {
                newStorage = null;
            }
            else
            {
                newStorage = new StorageConfig
                {
                    Backend = backend,
                    ConnectionString = connectionStr,
                    Schema = schema,
                    Database = database,
                    Isolation = isolation
                };
            }

            // Update manifest
            manifest = manifest with
            {
                Workspace = manifest.Workspace with { Storage = newStorage }
            };

            await File.WriteAllTextAsync(manifestPath, parser.Serialize(manifest), cancellationToken);

            AnsiConsole.WriteLine();
            if (newStorage is null)
            {
                CliTheme.WriteSuccess($"Workspace '{workspace}' reset to global default storage.");
            }
            else
            {
                CliTheme.WriteSuccess($"Workspace '{workspace}' storage set to {backend}.");
                if (isolation == StorageIsolation.Schema && !string.IsNullOrWhiteSpace(schema))
                    CliTheme.WriteKeyValue("Schema", schema);
                if (isolation == StorageIsolation.Database && !string.IsNullOrWhiteSpace(database))
                    CliTheme.WriteKeyValue("Database", database);
                CliTheme.WriteKeyValue("Isolation", isolation.ToString().ToLowerInvariant());
            }

            CliTheme.WriteMuted("  Start with: weave run " + workspace);

            return 0;
        });

        return cmd;
    }

    private static string MaskPassword(string connStr)
    {
        if (connStr.Contains("Password", StringComparison.OrdinalIgnoreCase))
            return System.Text.RegularExpressions.Regex.Replace(
                connStr, @"(Password\s*=\s*)[^;]+", "$1***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return connStr;
    }
}
