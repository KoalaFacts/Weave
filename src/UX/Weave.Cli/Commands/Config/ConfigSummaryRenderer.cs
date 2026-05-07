using Spectre.Console;
using Weave.Actions.Config;

namespace Weave.Cli.Commands;

/// <summary>
/// Spectre rendering of <see cref="ConfigSummary"/>. Shared between the
/// <c>weave config get</c> CLI command and the TUI <c>/config</c> view so both
/// surfaces print the same canonical table.
/// </summary>
internal static class ConfigSummaryRenderer
{
    public static void Render(ConfigSummary summary)
    {
        var table = CliTheme.CreateTable("Configuration");
        table.AddColumn(CliTheme.StyledColumn("Key"));
        table.AddColumn(CliTheme.StyledColumn("Value"));

        table.AddRow("version", summary.Version);
        table.AddRow("defaultPort", summary.DefaultPort);
        table.AddRow("storage", Markup.Escape(summary.Storage));
        table.AddRow("authMode", Markup.Escape(summary.AuthMode));
        table.AddRow("requireHttps", summary.RequireHttps);
        table.AddRow("siloPath", Markup.Escape(summary.SiloPath));
        table.AddRow("weaveHome", Markup.Escape(summary.WeaveHome));
        table.AddRow("baseUrl", Markup.Escape(summary.BaseUrl));

        AnsiConsole.Write(table);
    }
}
