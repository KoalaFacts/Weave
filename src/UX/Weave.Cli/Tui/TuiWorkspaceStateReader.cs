using Weave.Cli.Commands;

namespace Weave.Cli.Tui;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from TUI status views.")]
internal sealed class TuiWorkspaceStateReader
{
    public string? ReadWorkspaceId(string manifestPath)
    {
        var statePath = WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);
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
