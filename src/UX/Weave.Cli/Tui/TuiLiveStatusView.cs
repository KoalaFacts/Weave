using Spectre.Console;
using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;
namespace Weave.Cli.Tui;

internal static class TuiLiveStatusView
{
    public static async Task<bool> TryRenderOnceAsync(
        string manifestPath,
        WorkspaceManifest manifest,
        CancellationToken cancellationToken)
    {
        var workspaceId = TuiWorkspaceStateReader.ReadWorkspaceId(manifestPath);
        if (workspaceId is null)
            return false;

        try
        {
            using var client = new WorkspaceApiClient();
            if (!await client.IsReachableAsync(cancellationToken))
                return false;

            var workspace = await client.GetWorkspaceAsync(workspaceId, cancellationToken);
            var agents = await client.GetAgentsAsync(workspaceId, cancellationToken);
            var tools = await client.GetToolsAsync(workspaceId, cancellationToken);

            AnsiConsole.Write(TuiLiveStatusRenderer.Build(manifestPath, manifest, workspace, agents, tools));
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Net.Sockets.SocketException or System.Text.Json.JsonException or IOException)
        {
            CliTheme.WriteWarning($"Live status unavailable: {ex.Message}. Showing manifest instead.");
            return false;
        }
    }

    public static async Task WatchAsync(
        string manifestPath,
        WorkspaceManifest manifest,
        CancellationToken cancellationToken) =>
        await TuiLiveStatusWatcher.WatchAsync(manifestPath, manifest, cancellationToken);
}
