namespace Weave.Actions.Workspace;

/// <summary>
/// Result of <see cref="StopWorkspaceAction"/>. The silo returns 204 with no
/// body on success; the result carries no payload but lets the action share
/// the <c>ActionResult&lt;T&gt;</c> contract with every other verb.
/// </summary>
public sealed record StopWorkspaceResult;
