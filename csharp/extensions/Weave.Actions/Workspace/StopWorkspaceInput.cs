namespace Weave.Actions.Workspace;

/// <summary>
/// Input for <see cref="StopWorkspaceAction"/>. Frontends pass the workspace
/// id they recorded when the workspace was started; the action does not
/// resolve names.
/// </summary>
public sealed class StopWorkspaceInput(string workspaceId, string? capability = null)
{
    public string WorkspaceId { get; } = workspaceId;
    internal string? Capability { get; } = capability;

    public override string ToString() => "StopWorkspaceInput (capability: ***REDACTED***)";
}
