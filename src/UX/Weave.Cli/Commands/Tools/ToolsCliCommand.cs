using Weave.Actions.Context;
using Weave.Actions.Tool;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

/// <summary>
/// Phase 1 read-only verb on the CLI side. Resolves the workspace via the
/// standard guided/advanced prompt pattern, reads the state file to find the
/// live workspace id, then delegates to <see cref="ListToolsAction"/>. Falls
/// back to manifest-only rendering when the workspace isn't running or the
/// silo can't be reached.
/// </summary>
internal sealed class ToolsCliCommand : ICliCommand<WorkspaceNameOptions>
{
    private readonly ListToolsAction _action;

    public ToolsCliCommand(ListToolsAction action)
    {
        _action = action;
    }

    public string Name => "tools";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "List tools in a workspace";

    public async Task<int> ExecuteAsync(WorkspaceNameOptions options, CancellationToken ct)
    {
        var name = WorkspacePrompt.SelectName(options.Name, "Which workspace would you like to inspect?");
        var manifestPath = ManifestResolver.Resolve(name);
        if (manifestPath is null)
        {
            WorkspacePrompt.WriteManifestNotFound(name);
            return 1;
        }

        var manifest = await WorkspaceManifestFile.ReadAsync(manifestPath, ct);
        var statePath = WorkspaceApiClient.GetWorkspaceStatePath(manifestPath);
        if (File.Exists(statePath))
        {
            var workspaceId = (await File.ReadAllTextAsync(statePath, ct)).Trim();
            var result = await _action.ExecuteAsync(new ListToolsInput(workspaceId), ct);
            if (result.IsSuccess)
            {
                if (result.Value.Tools.Count > 0)
                {
                    ToolsRenderer.RenderLive(manifest.Name, result.Value.Tools);
                    return 0;
                }
                CliTheme.WriteWarning("No live tools reported by the silo.");
                return 0;
            }

            if (result.Failure.Reason == ActionFailureReason.Cancelled)
                return 130;

            if (result.Failure.Reason == ActionFailureReason.SiloUnreachable)
                CliTheme.WriteWarning($"{result.Failure.Message} Falling back to manifest data.");
            else
                CliTheme.WriteError(result.Failure.Message);
        }

        if (manifest.Tools is { Count: > 0 })
        {
            ToolsRenderer.RenderManifestTools(
                manifest.Name,
                manifest.Tools.Select(kvp => new KeyValuePair<string, string>(kvp.Key, kvp.Value.Type)));
            return 0;
        }

        CliTheme.WriteWarning("No tools declared in the manifest.");
        return 0;
    }
}
