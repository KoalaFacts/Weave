using Spectre.Console;

namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for CLI testability.")]
internal sealed class DataExportWorkspaceSelector
{
    public string? SelectWorkspace(string? workspace)
    {
        if (!string.IsNullOrWhiteSpace(workspace))
            return workspace;

        var all = WorkspaceRegistry.GetAll();
        if (all.Count == 0)
            return null;

        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Which workspace would you like to export?")
                .Styled()
                .AddChoices(all.Keys));
    }

    public string? ResolveManifestPath(string workspace) => ManifestResolver.Resolve(workspace);
}
