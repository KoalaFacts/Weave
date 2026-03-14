using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

public sealed class WorkspaceUpCommand : AsyncCommand<WorkspaceUpCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Workspace name")]
        public string Name { get; init; } = string.Empty;

        [CommandOption("--target <TARGET>")]
        [Description("Deployment target")]
        [DefaultValue("local")]
        public string Target { get; init; } = "local";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var manifestPath = ManifestResolver.Resolve(settings.Name);
        if (manifestPath is null)
        {
            AnsiConsole.MarkupLine($"[red]No workspace.yml found for '{settings.Name}'.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"Starting workspace from [bold]{manifestPath}[/] (target: {settings.Target})...");

        var yaml = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var parser = new ManifestParser();
        var manifest = WorkspaceApiClient.PrepareManifest(
            parser.Parse(yaml),
            Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Directory.GetCurrentDirectory());

        try
        {
            using var client = new WorkspaceApiClient();

            if (!await client.IsReachableAsync(cancellationToken))
            {
                AnsiConsole.MarkupLine("[red]Cannot reach the Weave server.[/]");
                AnsiConsole.MarkupLine("Start it first with: [bold]weave serve[/]");
                return 1;
            }

            var response = await client.StartWorkspaceAsync(manifest, cancellationToken);
            var statePath = WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            await File.WriteAllTextAsync(statePath, response.WorkspaceId, cancellationToken);

            AnsiConsole.MarkupLine($"  Workspace: [bold]{manifest.Name}[/]");
            AnsiConsole.MarkupLine($"  Workspace ID: [bold]{response.WorkspaceId}[/]");
            AnsiConsole.MarkupLine($"  Status: {response.Status}");
            AnsiConsole.MarkupLine($"  Agents: {manifest.Agents.Count}");
            AnsiConsole.MarkupLine($"  Tools: {manifest.Tools.Count}");
            AnsiConsole.MarkupLine("[green]Workspace started successfully.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to start workspace: {ex.Message}[/]");
            return 1;
        }

        return 0;
    }
}

internal static class ManifestResolver
{
    public static string? Resolve(string? workspace)
    {
        if (workspace is not null)
        {
            var path = Path.Combine("workspaces", workspace, "workspace.yml");
            return File.Exists(path) ? path : null;
        }

        if (File.Exists("workspace.yml"))
            return "workspace.yml";

        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "workspace.yml");
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
