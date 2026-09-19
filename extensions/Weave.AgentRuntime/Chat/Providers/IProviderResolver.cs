using Microsoft.Extensions.AI;
using Weave.Workspaces.Manifest;

namespace Weave.Agents.Pipeline.Providers;

/// <summary>Picks an <see cref="IChatClient"/> for an agent.</summary>
/// <remarks>
/// Implementations dispatch on the agent's manifest <see cref="AgentDefinition"/>
/// (provider override → model prefix), resolve the API key via
/// <see cref="IAgentSecretResolver"/> (when <c>ApiKeyRef</c> is set) or fall back to
/// <see cref="IAgentCredentialStore"/>, and apply the manifest's <c>BaseUrl</c>
/// override when present. When no provider matches or no credentials are
/// configured, the in-process echo client is returned so tests stay deterministic
/// and the silo doesn't crash on startup.
/// </remarks>
public interface IProviderResolver
{
    Task<IChatClient> ResolveAsync(string agentId, AgentDefinition? definition, CancellationToken ct = default);
}
