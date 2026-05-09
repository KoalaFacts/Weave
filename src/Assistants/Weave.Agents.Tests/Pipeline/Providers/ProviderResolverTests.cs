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
    [InlineData("claude-3-5-sonnet-20241022")]
    [InlineData("claude-opus-4-20250514")]
    public void Resolve_AnthropicPrefix_WithKey_ConsultsAnthropicCredentialStore(string modelId)
    {
        var providerLookups = new List<string>();
        var resolver = CreateResolver(name =>
        {
            providerLookups.Add(name);
            return name == "anthropic" ? "sk-ant-test" : null;
        });

        resolver.Resolve("agent-1", modelId);

        providerLookups.ShouldContain("anthropic");
    }

    [Theory]
    [InlineData("anything-else")]
    [InlineData("llama-3-1")]
    [InlineData(null)]
    public void Resolve_UnknownModel_DoesNotConsultCredentialStore(string? modelId)
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

    [Fact]
    public void Resolve_AnthropicPrefix_NoKey_FallsBackToEchoClient()
    {
        var resolver = CreateResolver(_ => null);

        var client = resolver.Resolve("agent-1", "claude-sonnet-4-20250514");

        client.ShouldBeOfType<FallbackChatClient>();
    }

    [Fact]
    public void Resolve_AnthropicPrefix_WithKey_ReturnsRealAnthropicClient()
    {
        var resolver = CreateResolver(name => name == "anthropic" ? "sk-ant-test" : null);

        var client = resolver.Resolve("agent-1", "claude-sonnet-4-20250514");

        client.ShouldNotBeOfType<FallbackChatClient>();
    }

    [Fact]
    public void Resolve_AnthropicPrefix_OnlyConsultsAnthropic_NotOpenAi()
    {
        var providerLookups = new List<string>();
        var resolver = CreateResolver(name =>
        {
            providerLookups.Add(name);
            return null;
        });

        resolver.Resolve("agent-1", "claude-sonnet-4-20250514");

        providerLookups.ShouldBe(["anthropic"]);
    }

    [Fact]
    public void Resolve_OpenAiPrefix_OnlyConsultsOpenAi_NotAnthropic()
    {
        var providerLookups = new List<string>();
        var resolver = CreateResolver(name =>
        {
            providerLookups.Add(name);
            return null;
        });

        resolver.Resolve("agent-1", "gpt-4o-mini");

        providerLookups.ShouldBe(["openai"]);
    }

    private static ProviderResolver CreateResolver(Func<string, string?> credentialBehavior) =>
        new(new SpyCredentialStore(credentialBehavior), NullLoggerFactory.Instance);

    private sealed class SpyCredentialStore(Func<string, string?> behavior) : IAgentCredentialStore
    {
        public string? GetApiKey(string providerName) => behavior(providerName);
    }
}
