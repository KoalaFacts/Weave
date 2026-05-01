using System.Text.Json;

namespace Weave.Cli.Commands;

internal static class CliConfigStore
{
    private static readonly CliSecretResolver SecretResolver = new();

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
        => SecretResolver.ResolveReference(reference);

    /// <summary>
    /// Wraps a raw connection string value as an env: reference and
    /// sets the environment variable hint.
    /// </summary>
    public static string ToEnvReference(string storageBackend)
        => SecretResolver.ToEnvReference(storageBackend);
}

