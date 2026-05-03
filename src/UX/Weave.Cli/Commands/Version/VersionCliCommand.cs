using Spectre.Console;

namespace Weave.Cli.Commands;

internal sealed class VersionCliCommand : ICliCommand<NoCliOptions>
{
    public string Name => "version";

    public IReadOnlyList<string> Aliases => ["v"];

    public string Description => "Show the installed weave version and cached update info";

    public Task<int> ExecuteAsync(NoCliOptions options, CancellationToken ct)
    {
        var current = VersionService.Current();
        var cache = VersionService.LoadCache();

        AnsiConsole.MarkupLine(
            $"weave [bold rgb({CliTheme.Primary.R},{CliTheme.Primary.G},{CliTheme.Primary.B})]v{Markup.Escape(current)}[/]");

        if (cache is null)
        {
            CliTheme.WriteMuted("  (no update cache yet — run `weave upgrade` to check now)");
            return Task.FromResult(0);
        }

        var newer = VersionService.IsNewer(cache.LatestVersion, current);
        if (newer)
        {
            AnsiConsole.MarkupLine(
                $"  [rgb({CliTheme.Warning.R},{CliTheme.Warning.G},{CliTheme.Warning.B})]" +
                $"↑ v{Markup.Escape(cache.LatestVersion)} is available[/] " +
                $"[rgb({CliTheme.Muted.R},{CliTheme.Muted.G},{CliTheme.Muted.B})]" +
                $"(checked {cache.CheckedAt.ToLocalTime():u})[/]");
            CliTheme.WriteMuted($"  Upgrade:  {VersionService.UpgradeCommand}");
        }
        else
        {
            CliTheme.WriteMuted(
                $"  latest on NuGet: v{cache.LatestVersion} (checked {cache.CheckedAt.ToLocalTime():u})");
        }

        return Task.FromResult(0);
    }
}
