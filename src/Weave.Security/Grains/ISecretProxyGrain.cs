using Orleans;
using Weave.Security.Tokens;

namespace Weave.Security.Grains;

/// <summary>
/// Grain that manages secret proxy routing for a workspace.
/// Keyed by workspaceId.
/// </summary>
public interface ISecretProxyGrain : IGrainWithStringKey
{
    Task<string> RegisterSecretAsync(string secretPath, CapabilityToken token);
    Task UnregisterSecretAsync(string secretPath);
    Task<string> SubstituteAsync(string content);
}
