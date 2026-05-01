namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for CLI testability.")]
internal sealed class DataExportWorkspaceSelector
{
    public string? SelectWorkspace(string? workspace)
        => WorkspacePrompt.SelectName(workspace, "Which workspace would you like to export?");

    public string? ResolveManifestPath(string? workspace) => ManifestResolver.Resolve(workspace);
}
