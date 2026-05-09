using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Pipeline;
using Weave.Agents.Pipeline.Providers;

namespace Weave.Agents.Tests.Pipeline.Providers;

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
        var resolver = CreateResolver(_ => null);

        var client = resolver.Resolve("agent-1", modelId);

        client.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("gpt-4o-mini")]
    [InlineData("gpt-4")]
    [InlineData("o1-preview")]
    [InlineData("o3-mini")]
    [InlineData("o4-mini")]
    public void Resolve_OpenAiPrefix_WithKey_ConsultsOpenAiCredentialStore(string modelId)
    {
        var providerLookups = new List<string>();
        var resolver = CreateResolver(name =>
        {
            providerLookups.Add(name);
            return name == "openai" ? "sk-test-openai" : null;
        });

        resolver.Resolve("agent-1", modelId);

        providerLookups.ShouldContain("openai");
    }

    [Theory]
    [InlineData("claude-sonnet-4-20250514")]
    [InlineData("anything-else")]
    [InlineData(null)]
    public void Resolve_NonOpenAiOrUnknownModel_DoesNotConsultCredentialStore(string? modelId)
    {
        var providerLookups = new List<string>();
        var resolver = CreateResolver(name =>
        {
            providerLookups.Add(name);
            return null;
        });

        resolver.Resolve("agent-1", modelId);

        providerLookups.ShouldBeEmpty();
    }

    [Fact]
    public void Resolve_OpenAiPrefix_NoKey_FallsBackToEchoClient()
    {
        var resolver = CreateResolver(_ => null);

        var client = resolver.Resolve("agent-1", "gpt-4o-mini");

        client.ShouldBeOfType<FallbackChatClient>();
    }

    [Fact]
    public void Resolve_OpenAiPrefix_WithKey_ReturnsRealOpenAiClient()
    {
        var resolver = CreateResolver(name => name == "openai" ? "sk-test-key" : null);

        var client = resolver.Resolve("agent-1", "gpt-4o-mini");

        client.ShouldNotBeOfType<FallbackChatClient>();
    }

    private static ProviderResolver CreateResolver(Func<string, string?> credentialBehavior) =>
        new(new SpyCredentialStore(credentialBehavior), NullLoggerFactory.Instance);

    private sealed class SpyCredentialStore(Func<string, string?> behavior) : IAgentCredentialStore
    {
        public string? GetApiKey(string providerName) => behavior(providerName);
    }
}
