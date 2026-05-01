using Spectre.Console;

namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for CLI testability.")]
internal sealed class RunWorkspaceSelector
{
    public RunWorkspaceSelection Select(string? name)
    {
        var manifestPath = ManifestResolver.Resolve(name);
        if (manifestPath is not null)
            return RunWorkspaceSelection.Run(name, manifestPath);

        CliTheme.WriteBanner();
        var existing = WorkspaceRegistry.GetAll();
        if (existing.Count > 0)
            return SelectExistingWorkspace(name, existing);

        SuggestNewWorkspace();
        return RunWorkspaceSelection.Stop(0);
    }

    private static RunWorkspaceSelection SelectExistingWorkspace(string? name, IReadOnlyDictionary<string, string> existing)
    {
        CliTheme.WriteInfo(name is null
            ? "No workspace.json found in the current directory."
            : $"Workspace '{name}' not found.");
        AnsiConsole.WriteLine();

        var choices = existing.Select(w => w.Key).ToList();
        choices.Add("Create a new workspace");

        var picked = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Which workspace would you like to run?")
                .Styled()
                .AddChoices(choices));

        if (picked == "Create a new workspace")
        {
            CliTheme.WriteMuted("  Run: weave workspace new <name>");
            return RunWorkspaceSelection.Stop(0);
        }

        var manifestPath = ManifestResolver.Resolve(picked);
        if (manifestPath is not null)
            return RunWorkspaceSelection.Run(picked, manifestPath);

        CliTheme.WriteError($"Workspace '{picked}' exists but has no workspace.json.");
        return RunWorkspaceSelection.Stop(1);
    }

    private static void SuggestNewWorkspace()
    {
        CliTheme.WriteInfo("No workspaces found. Let's create one.");
        AnsiConsole.WriteLine();

        var name = AnsiConsole.Prompt(
            new TextPrompt<string>("Workspace name:")
                .Styled()
                .DefaultValue("my-workspace"));

        var preset = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Choose a preset:")
                .Styled()
                .AddChoices([.. WorkspacePresets.All.Keys]));

        CliTheme.WriteMuted($"  Creating workspace '{name}' with preset '{preset}'...");
        CliTheme.WriteMuted($"  Run: weave workspace new {name} --preset {preset}");
        CliTheme.WriteMuted($"  Then: weave run {name}");
    }
}
