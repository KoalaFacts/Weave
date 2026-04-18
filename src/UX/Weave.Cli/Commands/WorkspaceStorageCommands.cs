using System.CommandLine;
using System.Globalization;
using System.Net.Sockets;
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

            // Defaults: database isolation, db name = "weave"
            var isolation = StorageIsolation.Database;
            if (!string.IsNullOrWhiteSpace(isolationStr))
            {
                isolation = isolationStr.Equals("schema", StringComparison.OrdinalIgnoreCase)
                    ? StorageIsolation.Schema
                    : StorageIsolation.Database;
            }

            database ??= "weave";

            // Connection string (only prompt for server backends)
            if (backend is not "memory" && string.IsNullOrWhiteSpace(connectionStr))
            {
                var defaultConn = backend switch
                {
                    "sqlite" => $"Data Source={Path.Combine(Path.GetDirectoryName(manifestPath)!, ".weave", "workspace.db")}",
                    "postgresql" => $"Host=localhost;Database={database};Username=weave;Password=weave",
                    "sqlserver" => $"Server=localhost;Database={database};Trusted_Connection=true;TrustServerCertificate=true",
                    "redis" => "localhost:6379",
                    _ => ""
                };

                connectionStr = AnsiConsole.Prompt(
                    new TextPrompt<string>("Connection string:")
                        .Styled()
                        .DefaultValue(defaultConn));
            }

            // Safety guard: check if database already exists
            if (backend is "postgresql" or "sqlserver" or "sqlite" && !string.IsNullOrWhiteSpace(connectionStr))
            {
                var dbExists = await CheckDatabaseExistsAsync(backend, connectionStr, database, cancellationToken);
                if (dbExists)
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
                        return 0;
                    }

                    if (action.StartsWith("rename", StringComparison.OrdinalIgnoreCase))
                    {
                        database = AnsiConsole.Prompt(
                            new TextPrompt<string>("New database name:")
                                .Styled());

                        // Update connection string with new database name
                        connectionStr = ReplaceDatabaseInConnectionString(backend, connectionStr, database);
                    }
                    // "override" — continue with the existing database
                }
            }

            // Schema (only for schema isolation on relational backends)
            if (backend is "postgresql" or "sqlserver" && isolation == StorageIsolation.Schema && string.IsNullOrWhiteSpace(schema))
            {
                schema = AnsiConsole.Prompt(
                    new TextPrompt<string>("Schema name:")
                        .Styled()
                        .DefaultValue(workspace));
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
                    Schema = isolation == StorageIsolation.Schema ? schema : null,
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
                CliTheme.WriteKeyValue("Database", database);
                CliTheme.WriteKeyValue("Isolation", isolation.ToString().ToLowerInvariant());
                if (isolation == StorageIsolation.Schema && !string.IsNullOrWhiteSpace(schema))
                    CliTheme.WriteKeyValue("Schema", schema);
            }

            CliTheme.WriteMuted("  Start with: weave run " + workspace);

            return 0;
        });

        return cmd;
    }

    private static async Task<bool> CheckDatabaseExistsAsync(string backend, string connectionString, string database, CancellationToken ct)
    {
        if (backend == "sqlite")
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                connectionString, @"Data Source\s*=\s*([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success && File.Exists(match.Groups[1].Value.Trim());
        }

        // For PostgreSQL/SQL Server, try a TCP connect as a proxy for "server reachable"
        // then assume the database might exist if we can connect
        try
        {
            string host;
            int port;
            if (backend == "postgresql")
                (host, port) = StorageCommands.ParseKvHostPort(connectionString, "Host", 5432);
            else if (backend == "sqlserver")
                (host, port) = StorageCommands.ParseSqlServerHostPort(connectionString);
            else
                return false;

            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(host, port, cts.Token);

            // Server is reachable — we can't cheaply check if the specific DB exists
            // without a full ADO.NET connection, so check if connection string already
            // has this database name embedded (meaning user is pointing at an existing db)
            return connectionString.Contains($"Database={database}", StringComparison.OrdinalIgnoreCase)
                || connectionString.Contains($"Initial Catalog={database}", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string ReplaceDatabaseInConnectionString(string backend, string connectionString, string newDatabase)
    {
        if (backend == "sqlite")
        {
            return System.Text.RegularExpressions.Regex.Replace(
                connectionString, @"(Data Source\s*=\s*)[^;]+",
                $"$1{newDatabase}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // PostgreSQL uses Database=, SQL Server uses Database= or Initial Catalog=
        var result = System.Text.RegularExpressions.Regex.Replace(
            connectionString, @"(Database\s*=\s*)[^;]+",
            $"$1{newDatabase}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        result = System.Text.RegularExpressions.Regex.Replace(
            result, @"(Initial Catalog\s*=\s*)[^;]+",
            $"$1{newDatabase}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return result;
    }

    private static string MaskPassword(string connStr)
    {
        if (connStr.Contains("Password", StringComparison.OrdinalIgnoreCase))
            return System.Text.RegularExpressions.Regex.Replace(
                connStr, @"(Password\s*=\s*)[^;]+", "$1***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return connStr;
    }
}
