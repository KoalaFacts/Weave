using Spectre.Console;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceShowCliCommand : ICliCommand<WorkspaceNameOptions>
{
    public string Name => "show";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Show workspace configuration";

    public async Task<int> ExecuteAsync(WorkspaceNameOptions options, CancellationToken ct)
    {
        var name = WorkspacePrompt.SelectName(options.Name, "Which workspace would you like to show?");
        var manifestPath = ManifestResolver.Resolve(name);
        if (manifestPath is null)
        {
            CliTheme.WriteError(name is null
                ? "No workspace.json found. Create one first with: weave workspace new"
                : $"No workspace.json found for '{name}'.");
            return 1;
        }

        var content = await File.ReadAllTextAsync(manifestPath, ct);
        AnsiConsole.Write(CliTheme.CreatePanel(content, "workspace.json"));
        return 0;
    }
}
