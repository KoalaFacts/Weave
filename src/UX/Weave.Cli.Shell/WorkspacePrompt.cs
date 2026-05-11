using Spectre.Console;

namespace Weave.Cli.Shell;

internal sealed class WorkspacePrompt(IWorkspaceRegistry registry, IManifestResolver manifestResolver)
{
    public string? SelectName(string? name, string title)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        if (manifestResolver.Resolve(null) is not null)
            return null;

        return SelectRegisteredName(null, title);
    }

    public string? SelectRegisteredName(string? name, string title)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        var workspaces = registry.GetAll();
        if (workspaces.Count == 0)
            return null;

        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(title)
                .Styled()
                .AddChoices(workspaces.Keys));
    }

    public static void WriteManifestNotFound(string? name)
    {
        CliTheme.WriteError(name is null
            ? "No workspace.json found. Create one first with: weave workspace new"
            : $"No workspace.json found for '{name}'.");
    }
}
