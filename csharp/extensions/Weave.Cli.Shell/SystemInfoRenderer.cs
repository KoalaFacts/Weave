using System.Globalization;
using Spectre.Console;
using Weave.Actions.SystemInfo;


namespace Weave.Cli.Shell;

internal static class SystemInfoRenderer
{
    public static void Render(SystemInfoResult info)
    {
        CliTheme.WriteSection("System info");

        var table = CliTheme.CreateTable();
        table.AddColumn(CliTheme.StyledColumn("Key"));
        table.AddColumn(CliTheme.StyledColumn("Value"));
        table.AddRow("Silo API",
            info.Reachable
                ? StatusMarkup.ColorTag(CliTheme.Success, $"online · {info.BaseUrl}")
                : StatusMarkup.ColorTag(CliTheme.Muted, $"offline · {info.BaseUrl}"));
        table.AddRow("Default port", info.DefaultPort.ToString(CultureInfo.InvariantCulture));
        table.AddRow("Storage", Markup.Escape(info.Storage));
        table.AddRow("Auth mode", Markup.Escape(info.AuthMode));
        table.AddRow("Require HTTPS", info.RequireHttps ? "true" : "false");
        table.AddRow("Silo path",
            string.IsNullOrWhiteSpace(info.SiloPath)
                ? StatusMarkup.ColorTag(CliTheme.Muted, "(auto-detect)")
                : Markup.Escape(info.SiloPath));
        table.AddRow("Weave home", Markup.Escape(info.WeaveHome));

        AnsiConsole.Write(table);
    }
}
