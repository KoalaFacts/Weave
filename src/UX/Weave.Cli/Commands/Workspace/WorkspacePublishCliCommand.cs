using Spectre.Console;
using Weave.Deploy;

namespace Weave.Cli.Commands;

internal sealed class WorkspacePublishCliCommand(IManifestResolver manifestResolver, WorkspacePrompt workspacePrompt) : ICliCommand<WorkspacePublishOptions>
{

    public string Name => "publish";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Generate deployment manifests";

    public async Task<int> ExecuteAsync(WorkspacePublishOptions options, CancellationToken ct)
    {
        var target = options.Target;
        var name = workspacePrompt.SelectName(options.Name, "Which workspace would you like to publish?");
        var manifestPath = manifestResolver.Resolve(name);
        if (manifestPath is null)
        {
            WorkspacePrompt.WriteManifestNotFound(name);
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

        var manifest = await WorkspaceManifestFile.ReadAsync(manifestPath, ct);

        IPublisher publisher = WorkspacePublisherFactory.Resolve(target);

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
