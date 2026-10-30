using System.Globalization;
using System.Text.RegularExpressions;

namespace Weave.Cli.Commands;

internal static partial class StorageConnectionStrings
{
    public static (string host, int port) ParseHostPort(string connectionString, int defaultPort)
    {
        var parts = connectionString.Split(',')[0].Split(':');
        var host = parts[0].Trim();
        var port = parts.Length > 1 && int.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultPort;
        return (host, port);
    }

    public static (string host, int port) ParseKeyValueHostPort(string connectionString, string hostKey, int defaultPort)
    {
        var host = "localhost";
        var port = defaultPort;

        foreach (var part in connectionString.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
                continue;

            var key = kv[0].Trim();
            var value = kv[1].Trim();
            if (key.Equals(hostKey, StringComparison.OrdinalIgnoreCase) || key.Equals("Server", StringComparison.OrdinalIgnoreCase))
                host = value;
            else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
                port = parsed;
        }

        return (host, port);
    }

    public static (string host, int port) ParseSqlServerHostPort(string connectionString)
    {
        var host = "localhost";
        var port = 1433;

        foreach (var part in connectionString.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
                continue;

            var key = kv[0].Trim();
            var value = kv[1].Trim();
            if (!key.Equals("Server", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("Data Source", StringComparison.OrdinalIgnoreCase))
                continue;

            var serverParts = value.Split(',', 2);
            host = serverParts[0].Trim();
            if (serverParts.Length > 1 && int.TryParse(serverParts[1].Trim(), CultureInfo.InvariantCulture, out var parsed))
                port = parsed;
        }

        return (host, port);
    }

    public static string MaskSecret(string connectionString)
    {
        return PasswordRegex().Replace(connectionString, match => match.Groups[1].Value + "***");
    }

    public static string? TryGetSqliteDataSource(string connectionString)
    {
        var match = DataSourceValueRegex().Match(connectionString);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    public static string ReplaceSqliteDataSource(string connectionString, string newDataSource)
    {
        return DataSourceAssignmentRegex().Replace(connectionString, match => match.Groups[1].Value + newDataSource);
    }

    public static string ReplaceDatabaseName(string connectionString, string newDatabase)
    {
        var result = DatabaseAssignmentRegex().Replace(connectionString, match => match.Groups[1].Value + newDatabase);
        return InitialCatalogAssignmentRegex().Replace(result, match => match.Groups[1].Value + newDatabase);
    }

    [GeneratedRegex(@"(Password\s*=\s*)[^;]+", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordRegex();

    [GeneratedRegex(@"Data Source\s*=\s*([^;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DataSourceValueRegex();

    [GeneratedRegex(@"(Data Source\s*=\s*)[^;]+", RegexOptions.IgnoreCase)]
    private static partial Regex DataSourceAssignmentRegex();

    [GeneratedRegex(@"(Database\s*=\s*)[^;]+", RegexOptions.IgnoreCase)]
    private static partial Regex DatabaseAssignmentRegex();

    [GeneratedRegex(@"(Initial Catalog\s*=\s*)[^;]+", RegexOptions.IgnoreCase)]
    private static partial Regex InitialCatalogAssignmentRegex();
}
