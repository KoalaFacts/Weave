using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Weave.Deploy;
using Weave.Deploy.Translators;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

public sealed class WorkspacePublishCommand : AsyncCommand<WorkspacePublishCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Workspace name")]
        public string Name { get; init; } = string.Empty;

        [CommandOption("--target <TARGET>")]
        [Description("Deployment target (docker-compose, kubernetes, nomad, fly-io, github-actions)")]
        public string? Target { get; init; }

        [CommandOption("--output <PATH>")]
        [Description("Output directory")]
        [DefaultValue("./output")]
        public string Output { get; init; } = "./output";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var manifestPath = ManifestResolver.Resolve(settings.Name);
        if (manifestPath is null)
        {
            AnsiConsole.MarkupLine($"[red]No workspace.yml found for '{settings.Name}'.[/]");
            return 1;
        }

        var target = settings.Target;
        if (string.IsNullOrWhiteSpace(target))
        {
            target = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select a deployment target:")
                    .AddChoices("docker-compose", "kubernetes", "nomad", "fly-io", "github-actions"));
        }

        var yaml = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var parser = new ManifestParser();
        var manifest = parser.Parse(yaml);

        IPublisher publisher = target switch
        {
            "docker-compose" => new DockerComposePublisher(),
            "kubernetes" or "k8s" => new KubernetesPublisher(),
            "nomad" => new NomadPublisher(),
            "fly-io" or "fly" => new FlyIoPublisher(),
            "github-actions" or "gh-actions" => new GitHubActionsPublisher(),
            _ => throw new ArgumentException($"Unknown target: {target}")
        };

        var options = new PublishOptions { OutputPath = settings.Output };
        var result = await publisher.PublishAsync(manifest, options, cancellationToken);

        if (result.Success)
        {
            AnsiConsole.MarkupLine($"[green]Published to {result.TargetName}:[/]");
            foreach (var file in result.GeneratedFiles)
                AnsiConsole.MarkupLine($"  {file}");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Publish failed: {result.Error}[/]");
            return 1;
        }

        return 0;
    }
}
