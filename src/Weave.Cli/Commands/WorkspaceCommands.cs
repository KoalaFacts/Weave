using System.ComponentModel;
using System.Globalization;
using System.Text;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Weave.Cli.Commands;

public sealed class WorkspaceNewCommand : AsyncCommand<WorkspaceNewCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Workspace name")]
        public string Name { get; init; } = string.Empty;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var name = settings.Name;
        var basePath = Path.Combine("workspaces", name);
        Directory.CreateDirectory(basePath);
        Directory.CreateDirectory(Path.Combine(basePath, "prompts"));
        Directory.CreateDirectory(Path.Combine(basePath, "data"));
        Directory.CreateDirectory(Path.Combine(basePath, ".weave"));

        var manifest = new StringBuilder();
        manifest.AppendLine(CultureInfo.InvariantCulture, $"version: \"1.0\"");
        manifest.AppendLine(CultureInfo.InvariantCulture, $"name: {name}");
        manifest.AppendLine();
        manifest.AppendLine("workspace:");
        manifest.AppendLine("  isolation: full");
        manifest.AppendLine("  network:");
        manifest.AppendLine(CultureInfo.InvariantCulture, $"    name: weave-{name}");
        manifest.AppendLine("  secrets:");
        manifest.AppendLine("    provider: env");
        manifest.AppendLine();
        manifest.AppendLine("agents:");
        manifest.AppendLine("  assistant:");
        manifest.AppendLine("    model: claude-sonnet-4-20250514");
        manifest.AppendLine("    system_prompt_file: ./prompts/assistant.md");
        manifest.AppendLine("    max_concurrent_tasks: 3");
        manifest.AppendLine("    tools: []");
        manifest.AppendLine();
        manifest.AppendLine("tools: {}");
        manifest.AppendLine();
        manifest.AppendLine("targets:");
        manifest.AppendLine("  local:");
        manifest.AppendLine("    runtime: podman");

        await File.WriteAllTextAsync(Path.Combine(basePath, "workspace.yml"), manifest.ToString(), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(basePath, "prompts", "assistant.md"),
            "# Assistant\n\nYou are a helpful AI assistant.\n", cancellationToken);

        AnsiConsole.MarkupLine($"[green]Workspace '{name}' created at {basePath}/[/]");
        AnsiConsole.MarkupLine("  workspace.yml — workspace manifest");
        AnsiConsole.MarkupLine("  prompts/assistant.md — agent system prompt");
        return 0;
    }
}

public sealed class WorkspaceListCommand : Command<WorkspaceListCommand.Settings>
{
    public sealed class Settings : CommandSettings { }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var workspacesDir = "workspaces";
        if (!Directory.Exists(workspacesDir))
        {
            AnsiConsole.MarkupLine("[yellow]No workspaces found.[/]");
            return 0;
        }

        var dirs = Directory.GetDirectories(workspacesDir);
        if (dirs.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No workspaces found.[/]");
            return 0;
        }

        var table = new Table();
        table.AddColumn("Name");
        table.AddColumn("Path");
        table.AddColumn("Status");

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            var hasManifest = File.Exists(Path.Combine(dir, "workspace.yml"));
            var status = hasManifest ? "[green]Ready[/]" : "[red]Invalid[/]";
            table.AddRow(name, dir, status);
        }

        AnsiConsole.Write(table);
        return 0;
    }
}

public sealed class WorkspaceRemoveCommand : Command<WorkspaceRemoveCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Workspace name")]
        public string Name { get; init; } = string.Empty;

        [CommandOption("--purge")]
        [Description("Delete workspace folder")]
        public bool Purge { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var path = Path.Combine("workspaces", settings.Name);
        if (!Directory.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Workspace '{settings.Name}' not found.[/]");
            return 1;
        }

        if (settings.Purge)
        {
            Directory.Delete(path, recursive: true);
            AnsiConsole.MarkupLine($"[green]Workspace '{settings.Name}' purged.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"Workspace '{settings.Name}' deregistered. Use [bold]--purge[/] to delete files.");
        }

        return 0;
    }
}
