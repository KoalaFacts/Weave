using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Pipeline;
using Weave.Agents.Pipeline.Providers;
using Weave.Workspaces.Manifest;

namespace Weave.Agents.Tests.Pipeline.Providers;

public class ProviderResolverTests
{
    [Theory]
    [InlineData("gpt-4o-mini")]
    [InlineData("claude-sonnet-4-20250514")]
    [InlineData("o1-preview")]
    [InlineData("anything-else")]
    [InlineData(null)]
    public async Task ResolveAsync_ReturnsNonNullChatClient(string? modelId)
    {
        var resolver = CreateResolver(_ => null);
        var definition = modelId is null ? null : new AgentDefinition { Model = modelId };

        var client = await resolver.ResolveAsync("agent-1", definition, TestContext.Current.CancellationToken);

        client.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("gpt-4o-mini")]
    [InlineData("gpt-4")]
    [InlineData("o1-preview")]
    [InlineData("o3-mini")]
    [InlineData("o4-mini")]
    public async Task ResolveAsync_OpenAiPrefix_WithKey_ConsultsOpenAiCredentialStore(string modelId)
    {
        var providerLookups = new List<string>();
        var resolver = CreateResolver(name =>
        {
            providerLookups.Add(name);
            return name == "openai" ? "sk-test-openai" : null;
        });

        await resolver.ResolveAsync("agent-1", new AgentDefinition { Model = modelId }, TestContext.Current.CancellationToken);

        providerLookups.ShouldContain("openai");
    }

    [Theory]
    [InlineData("claude-sonnet-4-20250514")]
    [InlineData("claude-3-5-sonnet-20241022")]
    [InlineData("claude-opus-4-20250514")]
    public async Task ResolveAsync_AnthropicPrefix_WithKey_ConsultsAnthropicCredentialStore(string modelId)
    {
        var providerLookups = new List<string>();
        var resolver = CreateResolver(name =>
        {
            providerLookups.Add(name);
            return name == "anthropic" ? "sk-ant-test" : null;
        });

        await resolver.ResolveAsync("agent-1", new AgentDefinition { Model = modelId }, TestContext.Current.CancellationToken);

        providerLookups.ShouldContain("anthropic");
    }

    [Theory]
    [InlineData("anything-else")]
    [InlineData("llama-3-1")]
    public async Task ResolveAsync_UnknownModel_DoesNotConsultCredentialStore(string modelId)
    {
        var providerLookups = new List<string>();
        var resolver = CreateResolver(name =>
        {
            providerLookups.Add(name);
            return null;
        });

        await resolver.ResolveAsync("agent-1", new AgentDefinition { Model = modelId }, TestContext.Current.CancellationToken);

        providerLookups.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_NullDefinition_DoesNotConsultCredentialStore()
    {
        var providerLookups = new List<string>();
        var resolver = CreateResolver(name =>
        {
            providerLookups.Add(name);
            return null;
        });

        await resolver.ResolveAsync("agent-1", null, TestContext.Current.CancellationToken);

        providerLookups.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_OpenAiPrefix_NoKey_FallsBackToEchoClient()
    {
        var resolver = CreateResolver(_ => null);

        var client = await resolver.ResolveAsync(
            "agent-1",
            new AgentDefinition { Model = "gpt-4o-mini" },
            TestContext.Current.CancellationToken);

        client.ShouldBeOfType<FallbackChatClient>();
    }

    [Fact]
    public async Task ResolveAsync_OpenAiPrefix_WithKey_ReturnsRealOpenAiClient()
    {
        var resolver = CreateResolver(name => name == "openai" ? "sk-test-key" : null);

        var client = await resolver.ResolveAsync(
            "agent-1",
            new AgentDefinition { Model = "gpt-4o-mini" },
            TestContext.Current.CancellationToken);

        client.ShouldNotBeOfType<FallbackChatClient>();
    }

    [Fact]
    public async Task ResolveAsync_AnthropicPrefix_NoKey_FallsBackToEchoClient()
    {
        var resolver = CreateResolver(_ => null);

        var client = await resolver.ResolveAsync(
            "agent-1",
            new AgentDefinition { Model = "claude-sonnet-4-20250514" },
            TestContext.Current.CancellationToken);

        client.ShouldBeOfType<FallbackChatClient>();
    }

    [Fact]
    public async Task ResolveAsync_AnthropicPrefix_WithKey_ReturnsRealAnthropicClient()
    {
        var resolver = CreateResolver(name => name == "anthropic" ? "sk-ant-test" : null);

        var client = await resolver.ResolveAsync(
            "agent-1",
            new AgentDefinition { Model = "claude-sonnet-4-20250514" },
            TestContext.Current.CancellationToken);

        client.ShouldNotBeOfType<FallbackChatClient>();
    }

    [Fact]
    public async Task ResolveAsync_AnthropicPrefix_OnlyConsultsAnthropic_NotOpenAi()
    {
        var providerLookups = new List<string>();
        var resolver = CreateResolver(name =>
        {
            providerLookups.Add(name);
            return null;
        });

        await resolver.ResolveAsync(
            "agent-1",
            new AgentDefinition { Model = "claude-sonnet-4-20250514" },
            TestContext.Current.CancellationToken);

        providerLookups.ShouldBe(["anthropic"]);
    }

    [Fact]
    public async Task ResolveAsync_OpenAiPrefix_OnlyConsultsOpenAi_NotAnthropic()
    {
        var providerLookups = new List<string>();
        var resolver = CreateResolver(name =>
        {
            providerLookups.Add(name);
            return null;
        });

        await resolver.ResolveAsync(
            "agent-1",
            new AgentDefinition { Model = "gpt-4o-mini" },
            TestContext.Current.CancellationToken);

        providerLookups.ShouldBe(["openai"]);
    }

    [Fact]
    public async Task ResolveAsync_ProviderOverride_BypassesPrefixDispatch()
    {
        var providerLookups = new List<string>();
        var resolver = CreateResolver(name =>
        {
            providerLookups.Add(name);
            return name == "anthropic" ? "sk-ant-test" : null;
        });

        var definition = new AgentDefinition
        {
            Model = "gpt-4o-mini",
            Provider = "anthropic"
        };

        await resolver.ResolveAsync("agent-1", definition, TestContext.Current.CancellationToken);

        providerLookups.ShouldBe(["anthropic"]);
    }

    [Fact]
    public async Task ResolveAsync_ApiKeyRef_UsesSecretResolver_InsteadOfCredentialStore()
    {
        var providerLookups = new List<string>();
        var resolverCalls = new List<string?>();
        var stubSecretResolver = new StubSecretResolver(p =>
        {
            resolverCalls.Add(p);
            return "sk-from-secret-resolver";
        });
        var resolver = CreateResolver(
            credentialBehavior: name =>
            {
                providerLookups.Add(name);
                return null;
            },
            secretResolver: stubSecretResolver);

        var definition = new AgentDefinition
        {
            Model = "claude-sonnet-4-20250514",
            ApiKeyRef = "{secret:env/<<MARKER-KEY-NAME>>}"
        };

        var client = await resolver.ResolveAsync("agent-1", definition, TestContext.Current.CancellationToken);

        resolverCalls.ShouldBe(["{secret:env/<<MARKER-KEY-NAME>>}"]);
        providerLookups.ShouldBeEmpty();
        client.ShouldNotBeOfType<FallbackChatClient>();
    }

    [Fact]
    public async Task ResolveAsync_ApiKeyRef_ResolverReturnsNull_FallsBack()
    {
        var resolverCalls = new List<string?>();
        var stubSecretResolver = new StubSecretResolver(p =>
        {
            resolverCalls.Add(p);
            return null;
        });
        var providerLookups = new List<string>();
        var resolver = CreateResolver(
            credentialBehavior: name =>
            {
                providerLookups.Add(name);
                return null;
            },
            secretResolver: stubSecretResolver);

        var definition = new AgentDefinition
        {
            Model = "claude-sonnet-4-20250514",
            ApiKeyRef = "{secret:env/MISSING_VAR}"
        };

        var client = await resolver.ResolveAsync("agent-1", definition, TestContext.Current.CancellationToken);

        resolverCalls.ShouldBe(["{secret:env/MISSING_VAR}"]);
        providerLookups.ShouldBeEmpty();
        client.ShouldBeOfType<FallbackChatClient>();
    }

    [Fact]
    public async Task ResolveAsync_AnthropicWithBaseUrl_ThreadsToClientMetadata()
    {
        var resolver = CreateResolver(name => name == "anthropic" ? "sk-ant-test" : null);

        var client = await resolver.ResolveAsync(
            "agent-1",
            new AgentDefinition
            {
                Model = "claude-sonnet-4-20250514",
                BaseUrl = "https://anthropic.example.test"
            },
            TestContext.Current.CancellationToken);

        var metadata = client.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata;
        metadata.ShouldNotBeNull();
        metadata!.ProviderUri!.AbsoluteUri.ShouldBe("https://anthropic.example.test/");
    }

    [Fact]
    public async Task ResolveAsync_AnthropicWithoutBaseUrl_DefaultsToAnthropicCom()
    {
        var resolver = CreateResolver(name => name == "anthropic" ? "sk-ant-test" : null);

        var client = await resolver.ResolveAsync(
            "agent-1",
            new AgentDefinition { Model = "claude-sonnet-4-20250514" },
            TestContext.Current.CancellationToken);

        var metadata = client.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata;
        metadata.ShouldNotBeNull();
        metadata!.ProviderUri!.AbsoluteUri.ShouldBe("https://api.anthropic.com/");
    }

    [Fact]
    public async Task ResolveAsync_NonHttpsBaseUrl_StillResolves_AndAppliesUrl()
    {
        var resolver = CreateResolver(name => name == "anthropic" ? "sk-ant-test" : null);

        var client = await resolver.ResolveAsync(
            "agent-1",
            new AgentDefinition
            {
                Model = "claude-sonnet-4-20250514",
                BaseUrl = "http://internal.example.test"
            },
            TestContext.Current.CancellationToken);

        client.ShouldNotBeOfType<FallbackChatClient>();
        var metadata = client.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata;
        metadata.ShouldNotBeNull();
        metadata!.ProviderUri!.AbsoluteUri.ShouldBe("http://internal.example.test/");
    }

    [Fact]
    public async Task ResolveAsync_UnknownProviderOverride_FallsBackBeforeResolvingSecret()
    {
        var resolverCalls = new List<string?>();
        var stubSecretResolver = new StubSecretResolver(p =>
        {
            resolverCalls.Add(p);
            return "sk-from-resolver";
        });
        var providerLookups = new List<string>();
        var resolver = CreateResolver(
            credentialBehavior: name =>
            {
                providerLookups.Add(name);
                return null;
            },
            secretResolver: stubSecretResolver);

        var definition = new AgentDefinition
        {
            Model = "claude-sonnet-4-20250514",
            Provider = "ollama",
            ApiKeyRef = "{secret:env/SOME_KEY}"
        };

        var client = await resolver.ResolveAsync("agent-1", definition, TestContext.Current.CancellationToken);

        resolverCalls.ShouldBeEmpty();
        providerLookups.ShouldBeEmpty();
        client.ShouldBeOfType<FallbackChatClient>();
    }

    private static ProviderResolver CreateResolver(
        Func<string, string?> credentialBehavior,
        IAgentSecretResolver? secretResolver = null)
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        var httpClientFactory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        return new(
            new SpyCredentialStore(credentialBehavior),
            secretResolver ?? new StubSecretResolver(_ => null),
            httpClientFactory,
            NullLoggerFactory.Instance);
    }

    private sealed class SpyCredentialStore(Func<string, string?> behavior) : IAgentCredentialStore
    {
        public string? GetApiKey(string providerName) => behavior(providerName);
    }

    private sealed class StubSecretResolver(Func<string, string?> behavior) : IAgentSecretResolver
    {
        public Task<string?> ResolveAsync(string? placeholder, CancellationToken ct = default) =>
            Task.FromResult(placeholder is null ? null : behavior(placeholder));
    }
}
