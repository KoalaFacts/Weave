using Spectre.Console;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceListCliCommand(IWorkspaceRegistry registry) : ICliCommand<NoCliOptions>
{
    public string Name => "list";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "List all workspaces";

    public Task<int> ExecuteAsync(NoCliOptions options, CancellationToken ct)
    {
        var workspaces = registry.GetAll();
        if (workspaces.Count == 0)
        {
            CliTheme.WriteWarning("No workspaces found.");
            return Task.FromResult(0);
        }

        var table = CliTheme.CreateTable("Workspaces");
        table.AddColumn(CliTheme.StyledColumn("Name"));
        table.AddColumn(CliTheme.StyledColumn("Path"));
        table.AddColumn(CliTheme.StyledColumn("Status"));

        foreach (var (name, dir) in workspaces)
        {
            var hasManifest = File.Exists(Path.Combine(dir, "workspace.json"));
            var status = hasManifest
                ? $"[rgb({CliTheme.Success.R},{CliTheme.Success.G},{CliTheme.Success.B})]Ready[/]"
                : Directory.Exists(dir)
                    ? $"[rgb({CliTheme.Error.R},{CliTheme.Error.G},{CliTheme.Error.B})]Invalid[/]"
                    : $"[rgb({CliTheme.Error.R},{CliTheme.Error.G},{CliTheme.Error.B})]Missing[/]";
            table.AddRow(name, dir, status);
        }

        AnsiConsole.Write(table);
        return Task.FromResult(0);
    }
}
