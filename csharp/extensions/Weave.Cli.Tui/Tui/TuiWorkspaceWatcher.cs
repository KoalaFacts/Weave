using Weave.Cli.Tui.Verbs;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tui;

internal sealed class TuiWorkspaceWatcher(TuiLiveStatusView liveStatus) : ITuiVerb
{
    private readonly ManifestParser _parser = new();

    public string Name => "watch";

    public IReadOnlyList<string> Aliases => [];

    public async Task DispatchAsync(TuiVerbContext context, CancellationToken ct)
    {
        var session = context.Session;
        if (!session.HasWorkspace)
        {
            CliTheme.WriteMuted("No workspace open. Try: /open <workspace>");
            return;
        }
        if (!session.IsRunning)
        {
            CliTheme.WriteMuted($"Workspace '{session.WorkspaceName}' is not running.");
            return;
        }

        WorkspaceManifest manifest;
        try
        {
            var json = await File.ReadAllTextAsync(session.ManifestPath!, ct);
            manifest = _parser.Parse(json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException)
        {
            CliTheme.WriteError($"Failed to parse manifest: {ex.Message}");
            return;
        }

        await liveStatus.WatchAsync(session.ManifestPath!, manifest, ct);
    }
}
