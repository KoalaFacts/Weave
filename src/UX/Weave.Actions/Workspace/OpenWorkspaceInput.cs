namespace Weave.Actions.Workspace;

/// <summary>
/// Input for <see cref="OpenWorkspaceAction"/>. Pass <c>null</c> or empty
/// to surface the registered-workspace selector via
/// <see cref="Context.IActionPrompter"/>; pass a name to resolve directly.
/// </summary>
public sealed record OpenWorkspaceInput(string? WorkspaceName);
