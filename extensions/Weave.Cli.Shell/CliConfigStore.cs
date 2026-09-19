using System.Text.Json;

namespace Weave.Cli.Shell;

internal sealed class CliConfigStore : IConfigStore
{
    private static readonly string WeaveHome = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");

    private static readonly string ConfigPath = Path.Combine(WeaveHome, "config.json");

    public CliConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return new CliConfig();

        var json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize(json, CliConfigJsonContext.Default.CliConfig)
            ?? new CliConfig();
    }

    public void Save(CliConfig config)
    {
        Directory.CreateDirectory(WeaveHome);
        var json = JsonSerializer.Serialize(config, CliConfigJsonContext.Default.CliConfig);
        File.WriteAllText(ConfigPath, json);
    }

    public bool Exists() => File.Exists(ConfigPath);
}
