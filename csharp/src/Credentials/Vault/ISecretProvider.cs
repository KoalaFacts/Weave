using Weave.Security.Tokens;
using Weave.Shared.Secrets;

namespace Weave.Security.Vault;

public interface ISecretProvider
{
    Task<SecretValue> ResolveAsync(string secretPath, CapabilityToken token, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListPathsAsync(string workspaceId, CancellationToken ct = default);
}
