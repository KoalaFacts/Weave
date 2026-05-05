namespace Weave.Actions;

/// <summary>
/// Minimal seam over the silo HTTP surface for the parts of an action that
/// only need to know "is the silo reachable". Phase 0 of the Shape C migration
/// keeps <c>WorkspaceApiClient</c> living in <c>Weave.Cli</c>; later phases
/// will move the full silo client into <c>Weave.Actions</c> and this interface
/// shrinks to the probe-only callers.
/// </summary>
public interface ISiloProbe
{
    Task<bool> IsReachableAsync(CancellationToken cancellationToken);
}
