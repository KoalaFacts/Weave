using Spectre.Console;
using Weave.Actions.AgentTask;
using Weave.Actions.Context;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Commands;

/// <summary>
/// Phase 1 read-only verb on the CLI side. Resolves the workspace + agent via
/// the standard guided/advanced prompt pattern, reads the state file to find
/// the live workspace id, then delegates to <see cref="ListTasksAction"/>.
/// Tasks are an agent-level concept — they only exist in the running silo, so
/// there's no manifest fallback.
/// </summary>
internal sealed class TasksCliCommand : ICliCommand<TasksOptions>
{
    private readonly ListTasksAction _action;

    public TasksCliCommand(ListTasksAction action)
    {
        _action = action;
    }

    public string Name => "tasks";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "List tasks for an agent in a workspace";

    public async Task<int> ExecuteAsync(TasksOptions options, CancellationToken ct)
    {
        var name = WorkspacePrompt.SelectName(options.Workspace, "Which workspace would you like to inspect?");
        var manifestPath = ManifestResolver.Resolve(name);
        if (manifestPath is null)
        {
            WorkspacePrompt.WriteManifestNotFound(name);
            return 1;
        }

        var manifest = await WorkspaceManifestFile.ReadAsync(manifestPath, ct);
        var agentName = SelectAgentName(options.Agent, manifest);
        if (agentName is null)
        {
            CliTheme.WriteError("No agents declared in the manifest.");
            return 1;
        }

        var statePath = WorkspaceManifestPaths.GetStatePath(manifestPath);
        if (!File.Exists(statePath))
        {
            CliTheme.WriteWarning("Workspace is not running. Start it with: weave workspace up");
            return 0;
        }

        var workspaceId = (await File.ReadAllTextAsync(statePath, ct)).Trim();
        var result = await _action.ExecuteAsync(new ListTasksInput(workspaceId, agentName), ct);
        if (result.IsSuccess)
        {
            if (result.Value.Tasks.Count > 0)
            {
                TasksRenderer.RenderLive(agentName, result.Value.Tasks);
                return 0;
            }
            CliTheme.WriteMuted($"No tasks for agent '{agentName}'.");
            return 0;
        }

        if (result.Failure.Reason == ActionFailureReason.Cancelled)
            return 130;

        if (result.Failure.Reason == ActionFailureReason.SiloUnreachable)
            CliTheme.WriteWarning(result.Failure.Message);
        else
            CliTheme.WriteError(result.Failure.Message);
        return 1;
    }

    private static string? SelectAgentName(string? supplied, WorkspaceManifest manifest)
    {
        if (!string.IsNullOrWhiteSpace(supplied))
            return supplied;

        if (manifest.Agents is not { Count: > 0 })
            return null;

        if (manifest.Agents.Count == 1)
            return manifest.Agents.Keys.First();

        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Which agent's tasks would you like to see?")
                .Styled()
                .AddChoices(manifest.Agents.Keys));
    }
}
