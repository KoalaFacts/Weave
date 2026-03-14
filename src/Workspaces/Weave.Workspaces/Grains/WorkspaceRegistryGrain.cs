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
        IReadOnlyList<string> workspaceIds = [.. persistentState.State.WorkspaceIds];
        return Task.FromResult(workspaceIds);
    }
}
