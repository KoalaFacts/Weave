namespace Weave.Silo.Security;

public sealed class ApiAuthOptions
{
    public const string ConfigSection = "Weave:Auth";

    public IApiAuthProvider? Provider { get; set; }
    public string Mode { get; set; } = "none";

    public static ApiAuthOptions FromConfiguration(IConfiguration configuration)
    {
        var mode = configuration[$"{ConfigSection}:Mode"]?.Trim().ToLowerInvariant() ?? "none";
        if (mode == "none")
            return new ApiAuthOptions { Mode = mode };

        if (mode is not ("apikey" or "bearer"))
            throw new InvalidOperationException("Weave:Auth:Mode must be none, apikey, or bearer.");

        var secret = configuration[$"{ConfigSection}:Secret"];
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("Weave:Auth:Secret is required when authentication is enabled.");

        var resolved = ResolveSecret(secret);
        if (string.IsNullOrWhiteSpace(resolved))
            throw new InvalidOperationException("Weave:Auth:Secret did not resolve to a non-empty secret.");

        IApiAuthProvider provider = mode == "apikey"
            ? new ApiKeyAuthProvider(resolved)
            : new BearerAuthProvider(resolved);
        return new ApiAuthOptions { Provider = provider, Mode = mode };
    }

    internal static string? ResolveSecret(string reference)
    {
        if (reference.StartsWith("vault:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Vault references are not supported by built-in API authentication; use env: or file:.");

        if (reference.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var varName = reference[4..].Trim();
            return Environment.GetEnvironmentVariable(varName);
        }

        if (reference.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var path = reference[5..].Trim();
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }

        return reference;
    }
}
