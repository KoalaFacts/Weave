using Microsoft.Extensions.AI;

namespace Weave.Agents.Pipeline.Providers;

/// <summary>Picks an <see cref="IChatClient"/> for an agent based on its model id.</summary>
/// <remarks>
/// Implementations dispatch by <c>modelId</c> prefix (<c>gpt-*</c>, <c>claude-*</c>, etc.)
/// and read credentials through <see cref="IAgentCredentialStore"/>. When no provider
/// matches or no credentials are configured, fall back to the in-process echo client
/// so tests stay deterministic and the silo doesn't crash on startup.
/// </remarks>
public interface IProviderResolver
{
    IChatClient Resolve(string agentId, string? modelId);
}
