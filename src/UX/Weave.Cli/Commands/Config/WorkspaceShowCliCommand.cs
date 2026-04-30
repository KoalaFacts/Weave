using Spectre.Console;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceShowCliCommand : ICliCommand<WorkspaceNameOptions>
{
    public string Name => "show";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Show workspace configuration";

    public async Task<int> ExecuteAsync(WorkspaceNameOptions options, CancellationToken ct)
    {
        var manifestPath = ManifestResolver.Resolve(options.Name);
        if (manifestPath is null)
        {
            CliTheme.WriteError($"No workspace.json found for '{options.Name}'.");
            return 1;
        }

        var content = await File.ReadAllTextAsync(manifestPath, ct);
        AnsiConsole.Write(CliTheme.CreatePanel(content, "workspace.json"));
        return 0;
    }
}
