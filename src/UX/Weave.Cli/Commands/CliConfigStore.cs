using System.Text.Json;
using System.Text.Json.Serialization;
using Weave.Shared;

namespace Weave.Cli.Commands;

internal sealed record CliConfig
{
    public string Version { get; init; } = "1.0";
    public string? SiloPath { get; init; }
    public int DefaultPort { get; init; } = WeavePorts.SiloHttp;
    public string Storage { get; init; } = "memory";
    public string? ConnectionString { get; init; }
}

internal static class CliConfigStore
{
    private static readonly string WeaveHome = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");

    private static readonly string ConfigPath = Path.Combine(WeaveHome, "config.json");

    public static CliConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return new CliConfig();

        var json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize(json, CliConfigJsonContext.Default.CliConfig)
            ?? new CliConfig();
    }

    public static void Save(CliConfig config)
    {
        Directory.CreateDirectory(WeaveHome);
        var json = JsonSerializer.Serialize(config, CliConfigJsonContext.Default.CliConfig);
        File.WriteAllText(ConfigPath, json);
    }

    public static bool Exists() => File.Exists(ConfigPath);

    /// <summary>
    /// Resolves a connection string reference to its actual value.
    /// Supported reference formats:
    ///   env:VAR_NAME          — read from environment variable
    ///   file:/path/to/secret  — read from a protected file
    ///   vault:secret/path     — read from Vault (requires Vault plugin)
    ///   (plain value)         — used as-is (not recommended for production)
    /// </summary>
    public static string? ResolveConnectionString(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;

        if (reference.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var varName = reference[4..].Trim();
            return Environment.GetEnvironmentVariable(varName)
                ?? throw new InvalidOperationException(
                    $"Environment variable '{varName}' is not set. Set it with: export {varName}=\"your-connection-string\"");
        }

        if (reference.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var filePath = reference[5..].Trim();
            if (!File.Exists(filePath))
                throw new InvalidOperationException(
                    $"Secret file '{filePath}' not found. Create it with your connection string.");
            return File.ReadAllText(filePath).Trim();
        }

        if (reference.StartsWith("vault:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Vault references require a running Weave server with the Vault plugin. "
                + "Use env: or file: references for CLI configuration.");
        }

        return reference;
    }

    /// <summary>
    /// Wraps a raw connection string value as an env: reference and
    /// sets the environment variable hint.
    /// </summary>
    public static string ToEnvReference(string storageBackend)
    {
        var varName = storageBackend.ToUpperInvariant() switch
        {
            "POSTGRESQL" or "POSTGRES" => "WEAVE_PG_CONNECTION",
            "SQLSERVER" => "WEAVE_SQL_CONNECTION",
            "REDIS" => "WEAVE_REDIS_CONNECTION",
            "SQLITE" => "WEAVE_SQLITE_CONNECTION",
            _ => "WEAVE_CONNECTION_STRING"
        };

        return $"env:{varName}";
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliConfig))]
internal sealed partial class CliConfigJsonContext : JsonSerializerContext;
