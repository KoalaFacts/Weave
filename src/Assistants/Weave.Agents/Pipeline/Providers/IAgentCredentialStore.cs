namespace Weave.Agents.Pipeline.Providers;

/// <summary>Resolves an LLM-provider API key for an agent.</summary>
/// <remarks>
/// The first cut reads only from process environment variables
/// (<c>OPENAI_API_KEY</c>, <c>ANTHROPIC_API_KEY</c>); manifest
/// <c>${secrets.X}</c> resolution layers on top in a follow-up.
/// Returning <c>null</c> means "no credential available" — the
/// resolver should fall back to the offline client.
/// </remarks>
public interface IAgentCredentialStore
{
    string? GetApiKey(string providerName);
}
