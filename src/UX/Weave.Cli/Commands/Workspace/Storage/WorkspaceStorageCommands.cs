using System.CommandLine;
using System.Net.Sockets;
using Spectre.Console;
using Weave.Workspaces.Manifest;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal static class WorkspaceStorageCommands
{
    internal static readonly string[] SupportedBackends = ["memory", "sqlite", "postgresql", "sqlserver", "redis"];

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
            return await new WorkspaceStorageShowCliCommand().ExecuteAsync(new WorkspaceNameOptions(workspace), cancellationToken);
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
            return await new WorkspaceStorageChangeCliCommand().ExecuteAsync(
                new WorkspaceStorageChangeOptions(workspace, backend, connectionStr, schema, database, isolationStr),
                cancellationToken);
        });

        return cmd;
    }

    internal static async Task<bool> CheckDatabaseExistsAsync(string backend, string connectionString, string database, CancellationToken ct)
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

    internal static string ReplaceDatabaseInConnectionString(string backend, string connectionString, string newDatabase)
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

    internal static string MaskPassword(string connStr)
    {
        if (connStr.Contains("Password", StringComparison.OrdinalIgnoreCase))
            return System.Text.RegularExpressions.Regex.Replace(
                connStr, @"(Password\s*=\s*)[^;]+", "$1***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return connStr;
    }
}
