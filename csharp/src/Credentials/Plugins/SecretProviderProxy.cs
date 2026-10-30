using Weave.Security.Tokens;
using Weave.Security.Vault;
using Weave.Shared.Plugins;
using Weave.Shared.Secrets;

namespace Weave.Security.Plugins;

/// <summary>
/// Proxy <see cref="ISecretProvider"/> registered as the singleton in DI.
/// Delegates to the broker's current secret provider if a plugin has swapped it in,
/// otherwise falls back to <see cref="InMemorySecretProvider"/>.
/// </summary>
public sealed class SecretProviderProxy(PluginServiceBroker broker, ISecretProvider fallback) : ISecretProvider
{
    private ISecretProvider Current => broker.Get<ISecretProvider>() ?? fallback;

    public Task<SecretValue> ResolveAsync(string secretPath, CapabilityToken token, CancellationToken ct = default)
        => Current.ResolveAsync(secretPath, token, ct);

    public Task<IReadOnlyList<string>> ListPathsAsync(string workspaceId, CancellationToken ct = default)
        => Current.ListPathsAsync(workspaceId, ct);
}
