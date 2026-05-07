using System.Text.Json;

namespace Weave.Cli.Shell;

internal static class CliSecretResolver
{
    public static string? ResolveReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;

        if (reference.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            return ResolveEnvironmentReference(reference[4..].Trim());

        if (reference.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return ResolveFileReference(reference[5..].Trim());

        if (reference.StartsWith("vault:", StringComparison.OrdinalIgnoreCase))
            return ResolveVaultSecret(reference[6..].Trim());

        return reference;
    }

    public static string ToEnvReference(string storageBackend)
    {
        var varName = storageBackend.ToUpperInvariant() switch
        {
            "POSTGRESQL" => "WEAVE_PG_CONNECTION",
            "SQLSERVER" => "WEAVE_SQL_CONNECTION",
            "REDIS" => "WEAVE_REDIS_CONNECTION",
            "SQLITE" => "WEAVE_SQLITE_CONNECTION",
            _ => "WEAVE_CONNECTION_STRING"
        };

        return $"env:{varName}";
    }

    private static string ResolveEnvironmentReference(string variableName) =>
        Environment.GetEnvironmentVariable(variableName)
        ?? throw new InvalidOperationException(
            $"Environment variable '{variableName}' is not set. Set it with: export {variableName}=\"your-connection-string\"");

    private static string ResolveFileReference(string filePath)
    {
        if (!File.Exists(filePath))
            throw new InvalidOperationException(
                $"Secret file '{filePath}' not found. Create it with your connection string.");

        return File.ReadAllText(filePath).Trim();
    }

    private static string ResolveVaultSecret(string secretPath)
    {
        var vaultAddr = Environment.GetEnvironmentVariable("VAULT_ADDR")
            ?? throw new InvalidOperationException(
                "VAULT_ADDR environment variable is not set. Set it to your Vault server address.");

        var vaultToken = Environment.GetEnvironmentVariable("VAULT_TOKEN")
            ?? throw new InvalidOperationException(
                "VAULT_TOKEN environment variable is not set. Set it to authenticate with Vault.");

        using var httpClient = new HttpClient { BaseAddress = new Uri(vaultAddr) };
        httpClient.DefaultRequestHeaders.Add("X-Vault-Token", vaultToken);

        using var response = httpClient.GetAsync($"/v1/{secretPath}").GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            throw new HttpRequestException($"Vault returned {(int)response.StatusCode}: {errorBody}");
        }

        using var doc = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        var data = doc.RootElement.GetProperty("data");

        if (data.TryGetProperty("data", out var nested) && nested.TryGetProperty("value", out var v2))
            return v2.GetString() ?? throw new KeyNotFoundException($"Vault secret '{secretPath}' has no value.");

        if (data.TryGetProperty("value", out var v1))
            return v1.GetString() ?? throw new KeyNotFoundException($"Vault secret '{secretPath}' has no value.");

        throw new KeyNotFoundException($"Vault secret '{secretPath}' not found or has no 'value' field.");
    }
}
