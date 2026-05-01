using System.Globalization;
using Spectre.Console;
using Weave.Cli.Commands;

namespace Weave.Cli.Tui;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from the TUI shell.")]
internal sealed class TuiConfigScreen
{
    public void Show()
    {
        var config = CliConfigStore.Load();

        CliTheme.WriteSection("CLI Configuration");

        var table = CliTheme.CreateTable();
        table.AddColumn(CliTheme.StyledColumn("Key"));
        table.AddColumn(CliTheme.StyledColumn("Value"));
        table.AddRow("defaultPort", config.DefaultPort.ToString(CultureInfo.InvariantCulture));
        table.AddRow("storage", Markup.Escape(config.Storage));
        table.AddRow("authMode", Markup.Escape(config.AuthMode));
        table.AddRow("requireHttps", config.RequireHttps ? "true" : "false");
        table.AddRow("siloPath",
            string.IsNullOrWhiteSpace(config.SiloPath)
                ? $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})](auto-detect)[/]"
                : Markup.Escape(config.SiloPath));

        AnsiConsole.Write(table);
        CliTheme.WriteMuted("Change settings with: weave config set <key> <value>");
    }
}
