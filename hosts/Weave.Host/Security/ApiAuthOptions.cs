namespace Weave.Silo.Security;

public sealed class ApiAuthOptions
{
    public const string ConfigSection = "Weave:Auth";

    public IApiAuthProvider? Provider { get; set; }
    public string Mode { get; set; } = "none";

    public static ApiAuthOptions FromConfiguration(IConfiguration configuration)
    {
        var mode = configuration[$"{ConfigSection}:Mode"]?.ToLowerInvariant() ?? "none";
        var secret = configuration[$"{ConfigSection}:Secret"];

        string? resolved = null;
        if (mode != "none" && !string.IsNullOrWhiteSpace(secret))
            resolved = ResolveSecret(secret);

        IApiAuthProvider? provider = mode switch
        {
            "apikey" when resolved is not null => new ApiKeyAuthProvider(resolved),
            "bearer" when resolved is not null => new BearerAuthProvider(resolved),
            _ => null
        };

        return new ApiAuthOptions { Provider = provider, Mode = mode };
    }

    private static string? ResolveSecret(string reference)
    {
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
