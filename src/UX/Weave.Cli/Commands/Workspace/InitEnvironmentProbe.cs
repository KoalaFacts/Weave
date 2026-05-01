using Spectre.Console;

namespace Weave.Cli.Commands;

internal static class InitEnvironmentProbe
{
    public static async Task<bool> TestConnectivityAsync(string storageKey, string connectionString, CancellationToken ct)
        => await StorageBackendService.TestConnectivityAsync(storageKey, connectionString, ct);

    public static string? PromptSiloPath()
    {
        var path = AnsiConsole.Prompt(
            new TextPrompt<string>("Path to Weave runtime (or press Enter to auto-detect later):")
                .Styled()
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(path))
            return null;

        path = Path.GetFullPath(path);

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            CliTheme.WriteWarning($"Path does not exist: {path}");
            CliTheme.WriteMuted("  Saving anyway — fix later with: weave config set siloPath <path>");
        }

        return path;
    }

    public static string? DetectSiloPath()
    {
        var candidates = new[]
        {
            Path.Combine("src", "Runtime", "Weave.Silo"),
            Path.Combine("src", "Runtime", "Weave.Silo", "Weave.Silo.csproj")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        var siloDll = Path.Combine(AppContext.BaseDirectory, "Weave.Silo.dll");
        return File.Exists(siloDll) ? siloDll : null;
    }
}
