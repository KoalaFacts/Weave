namespace Weave.Agents.Pipeline.Providers;

/// <summary>Resolves <c>{secret:backend/path}</c> placeholders to secret values.</summary>
/// <remarks>
/// Used by <see cref="ProviderResolver"/> to turn an agent's
/// <c>AgentDefinition.ApiKeyRef</c> placeholder into the actual key passed to the
/// upstream LLM client.
///
/// <para>Backends supported today:</para>
/// <list type="bullet">
///   <item><c>{secret:env/VAR_NAME}</c> — reads the silo process environment.</item>
/// </list>
///
/// <para>Backends recognized but deferred:</para>
/// <list type="bullet">
///   <item><c>{secret:vault/path}</c> — needs <c>CapabilityToken</c> plumbing through
///   the chat-client construction path. Tracked as P0.3. Resolves to <c>null</c>
///   today with a logged warning.</item>
/// </list>
///
/// <para>Literal values (no <c>{secret:</c> prefix) intentionally resolve to <c>null</c>
/// so a manifest cannot accidentally embed a hardcoded API key.</para>
/// </remarks>
public interface IAgentSecretResolver
{
    Task<string?> ResolveAsync(string? placeholder, CancellationToken ct = default);
}
