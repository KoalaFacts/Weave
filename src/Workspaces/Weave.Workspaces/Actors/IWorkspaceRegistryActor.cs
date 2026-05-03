namespace Weave.Workspaces.Actors;

public interface IWorkspaceRegistryActor
{
    Task RegisterAsync(string workspaceId);
    Task UnregisterAsync(string workspaceId);
    Task<IReadOnlyList<string>> GetWorkspaceIdsAsync();
}
