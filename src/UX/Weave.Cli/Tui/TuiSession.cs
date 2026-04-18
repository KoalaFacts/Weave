using Weave.Cli.Commands;

namespace Weave.Cli.Tui;

/// <summary>
/// In-memory REPL state: which workspace is "open" and which agent the
/// next free-form message should be routed to.
/// </summary>
internal sealed class TuiSession
{
    public string? WorkspaceName { get; private set; }
    public string? WorkspaceId { get; private set; }
    public string? ManifestPath { get; private set; }
    public string? AgentName { get; set; }

    public bool HasWorkspace => ManifestPath is not null;
    public bool IsRunning => WorkspaceId is not null;

    /// <summary>
    /// Resolves the workspace by registered name. Loads the workspace id
    /// from the state file if the workspace is currently running. Returns
    /// false (with <paramref name="error"/> populated) when the workspace
    /// cannot be opened at all.
    /// </summary>
    public bool TryOpen(string name, out string? error)
    {
        error = null;

        var manifestPath = ManifestResolver.Resolve(name);
        if (manifestPath is null)
        {
            error = $"No workspace.json found for '{name}'.";
            return false;
        }

        WorkspaceName = name;
        ManifestPath = manifestPath;
        WorkspaceId = TryReadWorkspaceId(manifestPath);
        AgentName = null;
        return true;
    }

    public void ClearAgent() => AgentName = null;

    /// <summary>Record that the Silo just started this workspace.</summary>
    public void MarkRunning(string workspaceId) => WorkspaceId = workspaceId;

    /// <summary>Record that the Silo just stopped this workspace.</summary>
    public void MarkStopped() => WorkspaceId = null;

    private static string? TryReadWorkspaceId(string manifestPath)
    {
        var statePath = WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);
        if (!File.Exists(statePath))
            return null;

        try
        {
            var id = File.ReadAllText(statePath).Trim();
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        catch
        {
            return null;
        }
    }
}
