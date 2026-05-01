using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tui;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from TUI agent views.")]
internal sealed class TuiAgentNameSource
{
    private readonly ManifestParser _parser = new();

    public async Task<List<string>> FetchAsync(TuiSession session, CancellationToken ct)
    {
        if (session.IsRunning)
        {
            try
            {
                using var client = new WorkspaceApiClient();
                if (await client.IsReachableAsync(ct))
                {
                    var live = await client.GetAgentsAsync(session.WorkspaceId!, ct);
                    return [.. live.Select(a => a.AgentName)];
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                CliTheme.WriteMuted($"Could not reach Silo for agent list ({ex.Message}). Falling back to manifest.");
            }
        }

        if (session.ManifestPath is null)
            return [];

        try
        {
            var manifest = _parser.Parse(File.ReadAllText(session.ManifestPath));
            return manifest.Agents is null ? [] : [.. manifest.Agents.Keys];
        }
        catch (Exception ex)
        {
            CliTheme.WriteMuted($"Could not read manifest for agent names ({ex.Message}).");
            return [];
        }
    }
}
