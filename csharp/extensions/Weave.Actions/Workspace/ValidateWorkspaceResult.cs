namespace Weave.Actions.Workspace;

/// <summary>
/// Result of <see cref="ValidateWorkspaceAction"/>: the parsed manifest's
/// summary plus any structural validation errors discovered. Errors-empty
/// means the manifest is valid; the action only emits an
/// <c>ActionFailure</c> when the manifest can't be read or parsed at all
/// (IO, bad JSON, etc.).
/// </summary>
public sealed record ValidateWorkspaceResult(
    string Name,
    int AgentCount,
    int ToolCount,
    int TargetCount,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}
