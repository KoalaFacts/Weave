using System.Globalization;
using Spectre.Console;
using Weave.Shared;

namespace Weave.Cli.Commands;

internal sealed class InitCliCommand(InitEnvironmentProbe? probe = null) : ICliCommand<NoCliOptions>
{
    private readonly InitEnvironmentProbe _probe = probe ?? new InitEnvironmentProbe();

    public string Name => "init";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Set up the Weave environment on this machine";

    public async Task<int> ExecuteAsync(NoCliOptions options, CancellationToken ct)
    {
        CliTheme.WriteBanner();
        AnsiConsole.MarkupLine("[bold]Setting up Weave on this machine.[/]");
        AnsiConsole.WriteLine();

        if (CliConfigStore.Exists())
        {
            var existing = CliConfigStore.Load();
            CliTheme.WriteInfo("Existing configuration found:");
            CliTheme.WriteKeyValue("Storage", existing.Storage);
            CliTheme.WriteKeyValue("Port", existing.DefaultPort.ToString(CultureInfo.InvariantCulture));
            CliTheme.WriteKeyValue("Silo path", existing.SiloPath ?? "(auto-detect)");
            AnsiConsole.WriteLine();

            if (!AnsiConsole.Confirm("Reconfigure?", defaultValue: false))
                return 0;

            AnsiConsole.WriteLine();
        }

        var storage = await InitStoragePrompt.PromptAsync(ct, _probe);

        // ── Step 2: Server port ──────────────────────────────────
        AnsiConsole.WriteLine();
        CliTheme.WriteSection("Step 2 · Server");

        var port = AnsiConsole.Prompt(
            new TextPrompt<int>("Server port:")
                .Styled()
                .DefaultValue(WeavePorts.SiloHttp));

        // ── Step 3: Silo path ────────────────────────────────────
        AnsiConsole.WriteLine();
        CliTheme.WriteSection("Step 3 · Runtime");

        var detectedSilo = _probe.DetectSiloPath();
        string? siloPath;

        if (detectedSilo is not null)
        {
            CliTheme.WriteInfo($"Detected runtime at: {detectedSilo}");
            siloPath = AnsiConsole.Confirm("Use this path?")
                ? detectedSilo
                : _probe.PromptSiloPath();
        }
        else
        {
            CliTheme.WriteMuted("No runtime detected in the current directory.");
            siloPath = _probe.PromptSiloPath();
        }

        var security = InitSecurityPrompt.Prompt();

        // ── Save ─────────────────────────────────────────────────
        var config = new CliConfig
        {
            SiloPath = siloPath,
            DefaultPort = port,
            Storage = storage.StorageKey,
            ConnectionString = storage.ConnectionString,
            AuthMode = security.AuthMode,
            AuthSecret = security.AuthSecret,
            RequireHttps = security.RequireHttps
        };

        CliConfigStore.Save(config);

        AnsiConsole.WriteLine();
        CliTheme.WriteSuccess("Environment configured.");
        CliTheme.WriteKeyValue("Config saved to", "~/.weave/config.json");
        CliTheme.WriteKeyValue("Storage", storage.StorageKey);
        CliTheme.WriteKeyValue("Port", port.ToString(CultureInfo.InvariantCulture));
        CliTheme.WriteKeyValue("Auth", security.AuthMode);
        if (security.RequireHttps)
            CliTheme.WriteKeyValue("HTTPS", "enforced");
        if (siloPath is not null)
            CliTheme.WriteKeyValue("Runtime", siloPath);

        // ── Next steps ───────────────────────────────────────────
        AnsiConsole.WriteLine();
        CliTheme.WriteSection("Next steps");
        AnsiConsole.WriteLine();
        CliTheme.WriteMuted("  1. Create a workspace:");
        CliTheme.WriteMuted("     weave workspace new my-app --preset coding-assistant");
        AnsiConsole.WriteLine();
        CliTheme.WriteMuted("  2. Run it:");
        CliTheme.WriteMuted("     weave run my-app");
        AnsiConsole.WriteLine();
        CliTheme.WriteMuted("  Or try the full-featured preset:");
        CliTheme.WriteMuted("     weave workspace new support --preset support-team");
        CliTheme.WriteMuted("     weave run support");
        AnsiConsole.WriteLine();
        CliTheme.WriteMuted("  Browse the marketplace:");
        CliTheme.WriteMuted("     weave marketplace list");

        return 0;
    }
}
