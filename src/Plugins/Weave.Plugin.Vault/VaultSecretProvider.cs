using Microsoft.Extensions.Logging;
using VaultSharp;
using VaultSharp.V1.AuthMethods.Token;
using Weave.Security.Tokens;
using Weave.Security.Vault;
using Weave.Shared.Secrets;

namespace Weave.Plugin.Vault;

public sealed partial class VaultSecretProvider : ISecretProvider
{
    private readonly IVaultClient _client;
    private readonly ICapabilityTokenService _tokenService;
    private readonly ILogger<VaultSecretProvider> _logger;

    public VaultSecretProvider(
        IVaultClient client,
        ICapabilityTokenService tokenService,
        ILogger<VaultSecretProvider> logger)
    {
        _client = client;
        _tokenService = tokenService;
        _logger = logger;
    }

    public async Task<SecretValue> ResolveAsync(string secretPath, CapabilityToken token, CancellationToken ct = default)
    {
        if (!_tokenService.Validate(token))
            throw new UnauthorizedAccessException($"Invalid or expired capability token for secret '{secretPath}'");

        if (!token.HasGrant($"secret:{secretPath}") && !token.HasGrant("secret:*"))
            throw new UnauthorizedAccessException($"Token does not grant access to secret '{secretPath}'");

        LogResolvingSecret(secretPath, token.IssuedTo, token.WorkspaceId);

        var secret = await _client.V1.Secrets.KeyValue.V2.ReadSecretAsync(
            path: secretPath,
            mountPoint: $"weave/{token.WorkspaceId}");

        var value = secret.Data.Data.TryGetValue("value", out var v) ? v?.ToString() : null;

        return string.IsNullOrEmpty(value)
            ? throw new KeyNotFoundException($"Secret '{secretPath}' not found or has no value")
            : new SecretValue(value);
    }

    public async Task<IReadOnlyList<string>> ListPathsAsync(string workspaceId, CancellationToken ct = default)
    {
        var result = await _client.V1.Secrets.KeyValue.V2.ReadSecretPathsAsync(
            path: "/",
            mountPoint: $"weave/{workspaceId}");

        return [.. result.Data.Keys];
    }

    public static IVaultClient CreateClient(string address, string token)
    {
        var auth = new TokenAuthMethodInfo(token);
        var settings = new VaultClientSettings(address, auth);
        return new VaultClient(settings);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Resolving secret '{Path}' for {IssuedTo} in workspace {Workspace}")]
    private partial void LogResolvingSecret(string path, string issuedTo, string workspace);
}
