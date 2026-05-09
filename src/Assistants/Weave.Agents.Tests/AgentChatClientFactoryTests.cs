using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Weave.Agents.Pipeline;
using Weave.Agents.Pipeline.Providers;
using Weave.Workspaces.Manifest;

namespace Weave.Agents.Tests;

/// <summary>
/// Unit tests for <see cref="AgentChatClientFactory"/>. Validates that the
/// factory composes a full chat client chain (fallback → rate-limiter →
/// cost-tracker → function-invocation middleware) and that the resulting
/// client can roundtrip a simple message end-to-end.
/// </summary>
public sealed class AgentChatClientFactoryTests
{
    private static AgentChatClientFactory CreateFactory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton<IAgentCostLedger, AgentCostLedger>();
        services.AddSingleton<IAgentCredentialStore, EnvironmentAgentCredentialStore>();
        services.AddSingleton<IAgentSecretResolver, AgentSecretResolver>();
        services.AddScoped<IProviderResolver, ProviderResolver>();
        services.AddScoped<IAgentChatClientFactory, AgentChatClientFactory>();
        var provider = services.BuildServiceProvider();

        return (AgentChatClientFactory)provider.GetRequiredService<IAgentChatClientFactory>();
    }

    [Fact]
    public async Task CreateAsync_ReturnsNonNullChatClient()
    {
        var factory = CreateFactory();

        var client = await factory.CreateAsync(
            "agent-1",
            new AgentDefinition { Model = "gpt-4o-mini" },
            TestContext.Current.CancellationToken);

        client.ShouldNotBeNull();
    }

    [Fact]
    public async Task CreateAsync_ResultingClient_ExecutesEndToEnd()
    {
        var factory = CreateFactory();
        var client = await factory.CreateAsync(
            "agent-1",
            new AgentDefinition { Model = "gpt-4o-mini" },
            TestContext.Current.CancellationToken);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions { ModelId = "gpt-4o-mini" },
            TestContext.Current.CancellationToken);

        response.ShouldNotBeNull();
        response.Text.ShouldContain("hello");
    }

    [Fact]
    public async Task CreateAsync_NullDefinition_FallsBackCleanly()
    {
        var factory = CreateFactory();

        var client = await factory.CreateAsync("agent-2", null, TestContext.Current.CancellationToken);

        client.ShouldNotBeNull();
    }

    [Fact]
    public async Task CreateAsync_ProducesIndependentClientsPerAgent()
    {
        var factory = CreateFactory();
        var def = new AgentDefinition { Model = "gpt-4o-mini" };

        var client1 = await factory.CreateAsync("agent-a", def, TestContext.Current.CancellationToken);
        var client2 = await factory.CreateAsync("agent-b", def, TestContext.Current.CancellationToken);

        client1.ShouldNotBeSameAs(client2);
        var r1 = await client1.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "1")],
            new ChatOptions { ModelId = "gpt-4o-mini" },
            TestContext.Current.CancellationToken);
        var r2 = await client2.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "2")],
            new ChatOptions { ModelId = "gpt-4o-mini" },
            TestContext.Current.CancellationToken);
        r1.Text.ShouldContain("1");
        r2.Text.ShouldContain("2");
    }
}
