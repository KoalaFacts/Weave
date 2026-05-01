using Spectre.Console;
using Weave.Deploy;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

internal sealed class WorkspacePublishCliCommand : ICliCommand<WorkspacePublishOptions>
{
    public string Name => "publish";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Generate deployment manifests";

    public async Task<int> ExecuteAsync(WorkspacePublishOptions options, CancellationToken ct)
    {
        var target = options.Target;
        var manifestPath = ManifestResolver.Resolve(options.Name);
        if (manifestPath is null)
        {
            CliTheme.WriteError($"No workspace.json found for '{options.Name}'.");
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

        var yaml = await File.ReadAllTextAsync(manifestPath, ct);
        var parser = new ManifestParser();
        var manifest = parser.Parse(yaml);

        IPublisher publisher = WorkspacePublishCommand.ResolvePublisher(target);

        var publishOptions = new PublishOptions { OutputPath = options.OutputPath };
        var result = await publisher.PublishAsync(manifest, publishOptions, ct);

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
    }
}
