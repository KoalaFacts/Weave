using System.CommandLine;
using System.Globalization;
using Spectre.Console;

namespace Weave.Cli.Commands;

internal static class InitCommand
{
    public static Command Create()
    {
        var cmd = new Command("init", "Set up Weave CLI defaults");
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            CliTheme.WriteBanner();

            if (CliConfigStore.Exists())
            {
                var existing = CliConfigStore.Load();
                CliTheme.WriteInfo("Existing configuration found:");
                CliTheme.WriteKeyValue("Version", existing.Version);
                CliTheme.WriteKeyValue("Silo path", existing.SiloPath ?? "(not set)");
                CliTheme.WriteKeyValue("Default port", existing.DefaultPort.ToString(CultureInfo.InvariantCulture));
                AnsiConsole.WriteLine();

                if (!AnsiConsole.Confirm("Reconfigure?", defaultValue: false))
                    return 0;
            }

            // ── Silo path ─────────────────────────────────────────
            var detectedSilo = DetectSiloPath();

            string? siloPath;
            if (detectedSilo is not null)
            {
                CliTheme.WriteInfo($"Detected silo at: {detectedSilo}");
                siloPath = AnsiConsole.Confirm("Use this path?")
                    ? detectedSilo
                    : PromptSiloPath();
            }
            else
            {
                siloPath = PromptSiloPath();
            }

            // ── Default port ──────────────────────────────────────
            var port = AnsiConsole.Prompt(
                new TextPrompt<int>("Default server port:")
                    .Styled()
                    .DefaultValue(9401));

            // ── Save ──────────────────────────────────────────────
            var config = new CliConfig
            {
                SiloPath = siloPath,
                DefaultPort = port
            };

            CliConfigStore.Save(config);
            CliTheme.WriteSuccess("Configuration saved to ~/.weave/config.json");
            AnsiConsole.WriteLine();

            // ── Offer to create first workspace ───────────────────
            if (!WorkspaceRegistry.GetAll().Any())
            {
                if (AnsiConsole.Confirm("No workspaces found. Create one now?"))
                {
                    var name = AnsiConsole.Prompt(
                        new TextPrompt<string>("Workspace name:")
                            .Styled()
                            .DefaultValue("my-workspace"));

                    // Delegate to `weave workspace new <name>` interactive flow
                    CliTheme.WriteMuted($"  Run: weave workspace new {name}");
                    CliTheme.WriteMuted("  This will walk you through preset selection and setup.");
                }
            }

            AnsiConsole.WriteLine();
            CliTheme.WriteSection("Next steps");
            CliTheme.WriteMuted("  weave workspace new <name>   Create a workspace");
            CliTheme.WriteMuted("  weave serve                  Start the local server");
            CliTheme.WriteMuted("  weave config get             Show current config");

            return 0;
        });

        return cmd;
    }

    private static string? PromptSiloPath()
    {
        var path = AnsiConsole.Prompt(
            new TextPrompt<string>("Path to silo project or published directory:")
                .Styled()
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(path))
            return null;

        path = Path.GetFullPath(path);

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            CliTheme.WriteWarning($"Path does not exist: {path}");
            CliTheme.WriteMuted("  Saving anyway — you can fix it later with: weave config set siloPath <path>");
        }

        return path;
    }

    private static string? DetectSiloPath()
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

        var exeDir = AppContext.BaseDirectory;
        var siloDll = Path.Combine(exeDir, "Weave.Silo.dll");
        if (File.Exists(siloDll))
            return siloDll;

        return null;
    }
}
