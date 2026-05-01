using Spectre.Console;

namespace Weave.Cli.Commands;

internal static class WorkspacePrompt
{
    public static string? SelectName(string? name, string title)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        if (ManifestResolver.Resolve(null) is not null)
            return null;

        return SelectRegisteredName(null, title);
    }

    public static string? SelectRegisteredName(string? name, string title)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        var workspaces = WorkspaceRegistry.GetAll();
        if (workspaces.Count == 0)
            return null;

        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(title)
                .Styled()
                .AddChoices(workspaces.Keys));
    }
}
