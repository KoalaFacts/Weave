using Weave.Security.Tokens;

namespace Weave.Security.Actors;

/// <summary>
/// Actor that manages secret proxy routing for a workspace.
/// Keyed by workspaceId.
/// </summary>
public interface ISecretProxyActor : IVirtualActorWithStringKey
{
    Task<string> RegisterSecretAsync(string secretPath, CapabilityToken token);
    Task UnregisterSecretAsync(string secretPath);
    Task<string> SubstituteAsync(string content);
}
