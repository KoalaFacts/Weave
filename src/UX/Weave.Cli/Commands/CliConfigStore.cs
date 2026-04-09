using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weave.Cli.Commands;

internal sealed record CliConfig
{
    public string Version { get; init; } = "1.0";
    public string? SiloPath { get; init; }
    public int DefaultPort { get; init; } = 9401;
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
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliConfig))]
internal sealed partial class CliConfigJsonContext : JsonSerializerContext;
