using Spectre.Console;
using Weave.Workspaces.Models;

namespace Weave.Cli.Commands;

internal sealed class WorkspaceNewCliCommand : ICliCommand<WorkspaceNewOptions>
{

    public string Name => "new";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Create a new workspace";

    public async Task<int> ExecuteAsync(WorkspaceNewOptions options, CancellationToken ct)
    {
        var name = options.Name;
        var preset = options.Preset;
        var explicitPath = options.Path;

        // Guided mode: prompt for missing name
        if (string.IsNullOrWhiteSpace(name))
        {
            CliTheme.WriteBanner();
            CliTheme.WriteInfo("Let's create a new workspace.");
            AnsiConsole.WriteLine();

            name = AnsiConsole.Prompt(
                new TextPrompt<string>("Workspace name:")
                    .Styled()
                    .DefaultValue("my-workspace"));
        }

        WorkspaceNewSelection selection;
        try
        {
            selection = WorkspaceNewSelectionPrompt.Select(preset);
        }
        catch (ArgumentException ex)
        {
            CliTheme.WriteError(ex.Message);
            return 1;
        }

        var basePath = Path.GetFullPath(explicitPath ?? name);
        Directory.CreateDirectory(basePath);
        Directory.CreateDirectory(Path.Combine(basePath, "prompts"));
        Directory.CreateDirectory(Path.Combine(basePath, "data"));
        Directory.CreateDirectory(Path.Combine(basePath, ".weave"));
        WorkspaceRegistry.Register(name, basePath);

        var template = WorkspaceNewTemplateFactory.Create(selection);

        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = name,
            Workspace = new WorkspaceConfig
            {
                Isolation = selection.Isolation,
                Network = new NetworkConfig { Name = $"weave-{name}" },
                Secrets = new SecretsConfig { Provider = "env" }
            },
            Agents = template.Agents,
            Tools = WorkspaceNewTemplateFactory.CreateTools(selection),
            Channels = WorkspaceNewTemplateFactory.CreateChannels(selection),
            Targets = new Dictionary<string, TargetDefinition>
            {
                ["local"] = new TargetDefinition { Runtime = "podman" }
            }
        };

        await WorkspaceManifestFile.WriteAsync(Path.Combine(basePath, "workspace.json"), manifest, ct);

        foreach (var (fileName, content) in template.PromptFiles)
        {
            await File.WriteAllTextAsync(Path.Combine(basePath, "prompts", fileName), content, ct);
        }

        CliTheme.WriteSuccess($"Workspace \"{name}\" created.");
        CliTheme.WriteMuted($"  Run `weave workspace up {name}` to start.");
        return 0;
    }
}
