

namespace Weave.Cli.Tui;

internal static class TuiWorkspaceStateReader
{
    public static string? ReadWorkspaceId(string manifestPath)
    {
        var statePath = WorkspaceManifestPaths.GetStatePath(manifestPath);
        if (!File.Exists(statePath))
            return null;

        try
        {
            var id = File.ReadAllText(statePath).Trim();
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
