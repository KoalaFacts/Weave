using System.Globalization;
using Spectre.Console;
using Weave.Actions.System;
using Weave.Cli.Tui;

namespace Weave.Cli.Commands;

/// <summary>
/// Pilot consumer of the Shape C action layer. Delegates to
/// <see cref="GetSystemInfoAction"/> for the data, then renders the typed
/// result through Spectre. The action knows nothing about the rendering.
/// </summary>
internal sealed class SystemCliCommand : ICliCommand<NoCliOptions>
{
    private readonly GetSystemInfoAction _action;

    public SystemCliCommand(GetSystemInfoAction action)
    {
        _action = action;
    }

    public string Name => "system";

    public IReadOnlyList<string> Aliases => ["sys"];

    public string Description => "Show silo and CLI config info";

    public async Task<int> ExecuteAsync(NoCliOptions options, CancellationToken ct)
    {
        var result = await _action.ExecuteAsync(new GetSystemInfoInput(), ct);
        if (!result.IsSuccess)
        {
            CliTheme.WriteError(result.Failure.Message);
            return 1;
        }

        Render(result.Value);
        return 0;
    }

    private static void Render(SystemInfoResult info)
    {
        CliTheme.WriteSection("System info");

        var table = CliTheme.CreateTable();
        table.AddColumn(CliTheme.StyledColumn("Key"));
        table.AddColumn(CliTheme.StyledColumn("Value"));
        table.AddRow("Silo API",
            info.Reachable
                ? TuiMarkup.ColorTag(CliTheme.Success, $"online · {info.BaseUrl}")
                : TuiMarkup.ColorTag(CliTheme.Muted, $"offline · {info.BaseUrl}"));
        table.AddRow("Default port", info.DefaultPort.ToString(CultureInfo.InvariantCulture));
        table.AddRow("Storage", Markup.Escape(info.Storage));
        table.AddRow("Auth mode", Markup.Escape(info.AuthMode));
        table.AddRow("Require HTTPS", info.RequireHttps ? "true" : "false");
        table.AddRow("Silo path",
            string.IsNullOrWhiteSpace(info.SiloPath)
                ? TuiMarkup.ColorTag(CliTheme.Muted, "(auto-detect)")
                : Markup.Escape(info.SiloPath));
        table.AddRow("Weave home", Markup.Escape(info.WeaveHome));

        AnsiConsole.Write(table);
    }
}
