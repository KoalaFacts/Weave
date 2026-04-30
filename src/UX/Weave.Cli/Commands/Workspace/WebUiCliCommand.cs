namespace Weave.Cli.Commands;

internal sealed record WebUiOptions(string? Url = null, bool NoOpen = false);

internal sealed class WebUiCliCommand : ICliCommand<WebUiOptions>
{
    public string Name => "webui";

    public IReadOnlyList<string> Aliases => ["web", "w"];

    public string Description => "Open the Weave web dashboard";

    public async Task<int> ExecuteAsync(WebUiOptions options, CancellationToken ct)
    {
        var url = options.Url ?? WebUiCommand.DefaultUrl();
        CliTheme.WriteKeyValue("Web UI", url);

        var reachable = await WebUiCommand.IsReachableAsync(url, ct);
        if (reachable)
            CliTheme.WriteSuccess("Dashboard is reachable.");
        else
            CliTheme.WriteWarning("Dashboard is not reachable yet. Start it with `weave run` or the AppHost.");

        if (options.NoOpen)
            return 0;

        if (WebUiCommand.TryOpenBrowser(url))
            CliTheme.WriteMuted("Opened in your default browser.");
        else
            CliTheme.WriteMuted($"Could not open a browser automatically. Visit: {url}");

        return 0;
    }
}
