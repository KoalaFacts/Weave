namespace Weave.Workspaces.Grains;

public interface IWorkspaceRegistryGrain : IGrainWithStringKey
{
    Task RegisterAsync(string workspaceId);
    Task UnregisterAsync(string workspaceId);
    Task<IReadOnlyList<string>> GetWorkspaceIdsAsync();
}
