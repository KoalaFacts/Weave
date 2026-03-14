using System.Text.Json;
using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Shared.Secrets;

namespace Weave.Security.Vault;

/// <summary>
/// HTTP-based HashiCorp Vault secret provider — no VaultSharp SDK required.
/// Calls the Vault HTTP API directly. Activated when a "vault" plugin is configured.
/// </summary>
public sealed partial class VaultSecretProvider(
    HttpClient httpClient,
    ICapabilityTokenService tokenService,
    ILogger<VaultSecretProvider> logger) : ISecretProvider
{
    public async Task<SecretValue> ResolveAsync(string secretPath, CapabilityToken token, CancellationToken ct = default)
    {
        if (!tokenService.Validate(token))
            throw new UnauthorizedAccessException($"Invalid or expired capability token for secret '{secretPath}'");

        if (!token.HasGrant($"secret:{secretPath}") && !token.HasGrant("secret:*"))
            throw new UnauthorizedAccessException($"Token does not grant access to secret '{secretPath}'");

        LogResolvingSecret(secretPath, token.IssuedTo, token.WorkspaceId);

        var mountPoint = $"weave/{token.WorkspaceId}";
        using var response = await httpClient.GetAsync($"/v1/{mountPoint}/data/{secretPath}", ct);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var data = doc.RootElement.GetProperty("data").GetProperty("data");

        var value = data.TryGetProperty("value", out var v) ? v.GetString() : null;

        return string.IsNullOrEmpty(value)
            ? throw new KeyNotFoundException($"Secret '{secretPath}' not found or has no value")
            : new SecretValue(value);
    }

    public async Task<IReadOnlyList<string>> ListPathsAsync(string workspaceId, CancellationToken ct = default)
    {
        var mountPoint = $"weave/{workspaceId}";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/{mountPoint}/metadata/?list=true");
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var keys = doc.RootElement.GetProperty("data").GetProperty("keys");

        var paths = new List<string>();
        foreach (var key in keys.EnumerateArray())
        {
            if (key.GetString() is { } k)
                paths.Add(k);
        }

        return paths;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Resolving secret '{Path}' for {IssuedTo} in workspace {Workspace}")]
    private partial void LogResolvingSecret(string path, string issuedTo, string workspace);
}
