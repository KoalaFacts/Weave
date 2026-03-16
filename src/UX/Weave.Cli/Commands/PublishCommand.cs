using System.CommandLine;
using Spectre.Console;
using Weave.Deploy;
using Weave.Deploy.Translators;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

internal static class WorkspacePublishCommand
{
    public static Command Create()
    {
        var nameArg = new Argument<string>("name") { Description = "Workspace name" };
        nameArg.CompletionSources.Add(CliCompletions.CompleteWorkspaceNames);
        var targetOption = new Option<string?>("--target") { Description = "Deployment target (docker-compose, kubernetes, nomad, fly-io, github-actions)" };
        targetOption.CompletionSources.Add(CliCompletions.CompleteDeployTargets);
        var outputOption = new Option<string>("--output")
        {
            Description = "Output directory",
            DefaultValueFactory = _ => "./output"
        };

        var cmd = new Command("publish", "Generate deployment manifests") { nameArg, targetOption, outputOption };
        cmd.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var target = parseResult.GetValue(targetOption);
            var output = parseResult.GetValue(outputOption)!;

            var manifestPath = ManifestResolver.Resolve(name);
            if (manifestPath is null)
            {
                CliTheme.WriteError($"No workspace.json found for '{name}'.");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                target = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Select a deployment target:")
                        .Styled()
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

            var options = new PublishOptions { OutputPath = output };
            var result = await publisher.PublishAsync(manifest, options, cancellationToken);

            if (result.Success)
            {
                CliTheme.WriteSuccess($"Published to {result.TargetName}:");
                foreach (var file in result.GeneratedFiles)
                    CliTheme.WriteMuted($"  {file}");
            }
            else
            {
                CliTheme.WriteError($"Publish failed: {result.Error}");
                return 1;
            }

            return 0;
        });

        return cmd;
    }
}
