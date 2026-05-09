using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Pipeline.Providers;

namespace Weave.Agents.Tests.Pipeline.Providers;

/// <summary>
/// Locks the contract that <see cref="ProviderResolver"/> always returns a
/// non-null <see cref="IChatClient"/>. Until OpenAI / Anthropic providers
/// land, every request resolves to the in-process fallback regardless of
/// the supplied credentials or model id.
/// </summary>
public class ProviderResolverTests
{
    [Theory]
    [InlineData("gpt-4o-mini")]
    [InlineData("claude-sonnet-4-20250514")]
    [InlineData("o1-preview")]
    [InlineData("anything-else")]
    [InlineData(null)]
    public void Resolve_ReturnsNonNullChatClient(string? modelId)
    {
        var resolver = CreateResolver();

        var client = resolver.Resolve("agent-1", modelId);

        client.ShouldNotBeNull();
    }

    [Fact]
    public void Resolve_DoesNotConsultCredentialStore_WhenFallbackOnly()
    {
        var calls = 0;
        var spyStore = new SpyCredentialStore(_ =>
        {
            calls++;
            return null;
        });
        var resolver = new ProviderResolver(spyStore, NullLoggerFactory.Instance);

        resolver.Resolve("agent-1", "gpt-4o-mini");

        // The first cut never asks for credentials because every request falls
        // back to the in-process echo client. This test will need to flip to
        // ShouldBeGreaterThan(0) once provider dispatch lands.
        calls.ShouldBe(0);
    }

    private static ProviderResolver CreateResolver() =>
        new(new EnvironmentAgentCredentialStore(_ => null), NullLoggerFactory.Instance);

    private sealed class SpyCredentialStore(Func<string, string?> behavior) : IAgentCredentialStore
    {
        public string? GetApiKey(string providerName) => behavior(providerName);
    }
}
