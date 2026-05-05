using Weave.Actions.Agent;

namespace Weave.Actions;

/// <summary>
/// Frontend-supplied seam over the silo HTTP surface. Grows verb-by-verb as
/// Phase 1 migrates read-only verbs into the action layer; the goal end-state
/// is for the silo HTTP client to live in <c>Weave.Actions</c> directly and
/// for this interface to disappear in favour of the concrete client.
/// </summary>
public interface ISiloApi
{
    Task<bool> IsReachableAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(string workspaceId, CancellationToken cancellationToken);
}
