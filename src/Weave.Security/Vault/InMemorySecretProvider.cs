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
    private readonly ICapabilityTokenService _tokenService;

    public InMemorySecretProvider(ICapabilityTokenService tokenService)
    {
        _tokenService = tokenService;
    }

    public void SetSecret(string path, string value)
    {
        _secrets[path] = new SecretValue(value);
    }

    public Task<SecretValue> ResolveAsync(string secretPath, CapabilityToken token, CancellationToken ct = default)
    {
        if (!_tokenService.Validate(token))
            throw new UnauthorizedAccessException("Invalid or expired capability token");

        if (!token.HasGrant($"secret:{secretPath}") && !token.HasGrant("secret:*"))
            throw new UnauthorizedAccessException($"Token does not grant access to secret '{secretPath}'");

        return _secrets.TryGetValue(secretPath, out var value)
            ? Task.FromResult(value)
            : throw new KeyNotFoundException($"Secret '{secretPath}' not found");
    }

    public Task<IReadOnlyList<string>> ListPathsAsync(string workspaceId, CancellationToken ct = default)
    {
        IReadOnlyList<string> paths = _secrets.Keys.ToList();
        return Task.FromResult(paths);
    }
}
