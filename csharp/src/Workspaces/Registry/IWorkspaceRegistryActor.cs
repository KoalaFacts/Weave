namespace Weave.Workspaces.Registry;

public interface IWorkspaceRegistryActor
{
    Task RegisterAsync(string workspaceId);
    Task UnregisterAsync(string workspaceId);
    Task<IReadOnlyList<string>> GetWorkspaceIdsAsync();
}
