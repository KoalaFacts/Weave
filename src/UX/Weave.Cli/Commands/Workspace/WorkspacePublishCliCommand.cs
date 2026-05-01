using Spectre.Console;
using Weave.Deploy;

namespace Weave.Cli.Commands;

internal sealed class WorkspacePublishCliCommand(
    WorkspacePublisherFactory? publishers = null,
    WorkspaceManifestFile? manifests = null) : ICliCommand<WorkspacePublishOptions>
{
    private readonly WorkspacePublisherFactory _publishers = publishers ?? new WorkspacePublisherFactory();
    private readonly WorkspaceManifestFile _manifests = manifests ?? new WorkspaceManifestFile();

    public string Name => "publish";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Generate deployment manifests";

    public async Task<int> ExecuteAsync(WorkspacePublishOptions options, CancellationToken ct)
    {
        var target = options.Target;
        var name = WorkspacePrompt.SelectName(options.Name, "Which workspace would you like to publish?");
        var manifestPath = ManifestResolver.Resolve(name);
        if (manifestPath is null)
        {
            CliTheme.WriteError(name is null
                ? "No workspace.json found. Create one first with: weave workspace new"
                : $"No workspace.json found for '{name}'.");
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

        var manifest = await _manifests.ReadAsync(manifestPath, ct);

        IPublisher publisher = _publishers.Resolve(target);

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
