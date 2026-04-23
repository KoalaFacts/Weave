using Weave.Workspaces.Models;

namespace Weave.Workspaces.Grains;

public sealed class WorkspaceRegistryGrain(
    [PersistentState("workspace-registry", "Default")] IPersistentState<WorkspaceRegistryState> persistentState)
    : Grain, IWorkspaceRegistryGrain
{
    public override Task OnActivateAsync(CancellationToken cancellationToken) =>
        persistentState.ReadStateAsync(cancellationToken);

    public async Task RegisterAsync(string workspaceId)
    {
        if (!persistentState.State.WorkspaceIds.Contains(workspaceId, StringComparer.Ordinal))
        {
            persistentState.State.WorkspaceIds.Add(workspaceId);
            await persistentState.WriteStateAsync();
        }
    }

    public async Task UnregisterAsync(string workspaceId)
    {
        if (persistentState.State.WorkspaceIds.Remove(workspaceId))
            await persistentState.WriteStateAsync();
    }

    public Task<IReadOnlyList<string>> GetWorkspaceIdsAsync()
    {
        // Assign to List<T> (not IReadOnlyList<T>) so the collection expression
        // emits a real List, not a synthesized <>z__ReadOnlyArray that Orleans
        // has no codec for. Same pattern as CapabilityTemplateGrain — see
        // docs/best-practices.md "Every grain method that returns a collection".
        List<string> workspaceIds = [.. persistentState.State.WorkspaceIds];
        return Task.FromResult<IReadOnlyList<string>>(workspaceIds);
    }
}
