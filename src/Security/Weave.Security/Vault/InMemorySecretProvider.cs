using System.Collections.Concurrent;
using Weave.Security.Tokens;
using Weave.Shared.Secrets;

namespace Weave.Security.Vault;

/// <summary>
/// In-memory secret provider for local development and testing.
/// </summary>
public sealed class InMemorySecretProvider : ISecretProvider
{
    private readonly ConcurrentDictionary<string, SecretValue> _secrets = new();
    private readonly ICapabilityAuthorizer _authorizer;

    public InMemorySecretProvider(ICapabilityAuthorizer authorizer)
    {
        _authorizer = authorizer;
    }

    public void SetSecret(string path, string value)
    {
        _secrets[path] = new SecretValue(value);
    }

    public async Task<SecretValue> ResolveAsync(string secretPath, CapabilityToken token, CancellationToken ct = default)
    {
        await _authorizer.AuthorizeAsync(token, $"secret:{secretPath}", token.WorkspaceId);

        return _secrets.TryGetValue(secretPath, out var value)
            ? value
            : throw new KeyNotFoundException($"Secret '{secretPath}' not found");
    }

    public Task<IReadOnlyList<string>> ListPathsAsync(string workspaceId, CancellationToken ct = default)
    {
        IReadOnlyList<string> paths = _secrets.Keys.ToList();
        return Task.FromResult(paths);
    }
}
