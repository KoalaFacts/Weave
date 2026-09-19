namespace Weave.Agents.Pipeline.Providers;

/// <summary>Reads provider API keys from process environment variables.</summary>
/// <remarks>
/// Maps <c>"openai"</c> to <c>OPENAI_API_KEY</c> and <c>"anthropic"</c> to
/// <c>ANTHROPIC_API_KEY</c>. Whitespace-only values count as missing.
/// The env-reader delegate is injectable so tests can avoid mutating
/// process-global state (xUnit v3 parallelizes test classes; concurrent
/// mutation of <c>Environment.SetEnvironmentVariable</c> races).
/// </remarks>
public sealed class EnvironmentAgentCredentialStore(Func<string, string?>? envReader = null) : IAgentCredentialStore
{
    private readonly Func<string, string?> _envReader = envReader ?? Environment.GetEnvironmentVariable;

    public string? GetApiKey(string providerName)
    {
        var value = providerName switch
        {
            "openai" => _envReader("OPENAI_API_KEY"),
            "anthropic" => _envReader("ANTHROPIC_API_KEY"),
            _ => null
        };
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
