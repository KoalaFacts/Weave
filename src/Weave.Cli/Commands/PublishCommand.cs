using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Weave.Deploy;
using Weave.Deploy.Translators;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

public sealed class PublishCommand : AsyncCommand<PublishCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<target>")]
        [Description("Deployment target (docker-compose, kubernetes, nomad, fly-io, github-actions)")]
        public string Target { get; init; } = string.Empty;

        [CommandOption("--output <PATH>")]
        [Description("Output directory")]
        [DefaultValue("./output")]
        public string Output { get; init; } = "./output";

        [CommandOption("--workspace <WORKSPACE>")]
        [Description("Workspace name")]
        public string? Workspace { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var manifestPath = ManifestResolver.Resolve(settings.Workspace);
        if (manifestPath is null)
        {
            AnsiConsole.MarkupLine("[red]No workspace.yml found.[/]");
            return 1;
        }

        var yaml = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var parser = new ManifestParser();
        var manifest = parser.Parse(yaml);

        IPublisher publisher = settings.Target switch
        {
            "docker-compose" => new DockerComposePublisher(),
            "kubernetes" or "k8s" => new KubernetesPublisher(),
            "nomad" => new NomadPublisher(),
            "fly-io" or "fly" => new FlyIoPublisher(),
            "github-actions" or "gh-actions" => new GitHubActionsPublisher(),
            _ => throw new ArgumentException($"Unknown target: {settings.Target}")
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
