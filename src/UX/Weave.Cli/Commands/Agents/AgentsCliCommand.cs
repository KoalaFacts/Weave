using Spectre.Console;
using Weave.Actions.Agent;
using Weave.Actions.Context;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

/// <summary>
/// First Phase 1 read-only verb on the CLI side. Resolves the workspace via
/// the standard guided/advanced prompt pattern, reads the state file to find
/// the live workspace id, then delegates to <see cref="ListAgentsAction"/>.
/// Falls back to manifest-only rendering when the workspace isn't running or
/// the silo can't be reached.
/// </summary>
internal sealed class AgentsCliCommand : ICliCommand<WorkspaceNameOptions>
{
    private readonly ListAgentsAction _action;

    public AgentsCliCommand(ListAgentsAction action)
    {
        _action = action;
    }

    public string Name => "agents";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "List agents in a workspace";

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
        var statePath = WorkspaceManifestPaths.GetStatePath(manifestPath);
        if (File.Exists(statePath))
        {
            var workspaceId = (await File.ReadAllTextAsync(statePath, ct)).Trim();
            var result = await _action.ExecuteAsync(new ListAgentsInput(workspaceId), ct);
            if (result.IsSuccess)
            {
                if (result.Value.Agents.Count > 0)
                {
                    AgentsRenderer.RenderLive(manifest.Name, result.Value.Agents, selectedAgent: null);
                    return 0;
                }
                CliTheme.WriteWarning("No live agents reported by the silo.");
                return 0;
            }

            if (result.Failure.Reason == ActionFailureReason.Cancelled)
                return 130;

            if (result.Failure.Reason == ActionFailureReason.SiloUnreachable)
                CliTheme.WriteWarning($"{result.Failure.Message} Falling back to manifest data.");
            else
                CliTheme.WriteError(result.Failure.Message);
        }

        if (manifest.Agents is { Count: > 0 })
        {
            AgentsRenderer.RenderManifestNames(manifest.Name, manifest.Agents.Keys, selectedAgent: null);
            return 0;
        }

        CliTheme.WriteWarning("No agents declared in the manifest.");
        return 0;
    }
}
