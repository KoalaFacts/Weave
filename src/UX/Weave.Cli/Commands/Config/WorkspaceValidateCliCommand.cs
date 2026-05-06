using System.Globalization;
using Weave.Actions.Context;
using Weave.Actions.Workspace;

namespace Weave.Cli.Commands;

/// <summary>
/// Phase 1 read-only verb on the CLI side. Resolves the manifest path via
/// the standard guided/advanced prompt pattern, then delegates parsing +
/// validation to <see cref="ValidateWorkspaceAction"/>.
/// </summary>
internal sealed class WorkspaceValidateCliCommand : ICliCommand<WorkspaceNameOptions>
{
    private readonly ValidateWorkspaceAction _action;

    public WorkspaceValidateCliCommand(ValidateWorkspaceAction action)
    {
        _action = action;
    }

    public string Name => "validate";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Validate workspace configuration";

    public async Task<int> ExecuteAsync(WorkspaceNameOptions options, CancellationToken ct)
    {
        var name = WorkspacePrompt.SelectName(options.Name, "Which workspace would you like to validate?");
        var manifestPath = ManifestResolver.Resolve(name);
        if (manifestPath is null)
        {
            WorkspacePrompt.WriteManifestNotFound(name);
            return 1;
        }

        var result = await _action.ExecuteAsync(new ValidateWorkspaceInput(manifestPath), ct);
        if (!result.IsSuccess)
        {
            if (result.Failure.Reason == ActionFailureReason.Cancelled)
                return 130;
            CliTheme.WriteError($"Configuration invalid: {result.Failure.Message}");
            return 1;
        }

        if (!result.Value.IsValid)
        {
            CliTheme.WriteError("Configuration invalid:");
            foreach (var error in result.Value.Errors)
                CliTheme.WriteMuted($"  - {error}");
            return 1;
        }

        CliTheme.WriteSuccess("Configuration valid.");
        CliTheme.WriteKeyValue("Name", result.Value.Name);
        CliTheme.WriteKeyValue("Agents", result.Value.AgentCount.ToString(CultureInfo.InvariantCulture));
        CliTheme.WriteKeyValue("Tools", result.Value.ToolCount.ToString(CultureInfo.InvariantCulture));
        CliTheme.WriteKeyValue("Targets", result.Value.TargetCount.ToString(CultureInfo.InvariantCulture));
        return 0;
    }
}
