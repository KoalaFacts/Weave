using Microsoft.Extensions.Logging.Abstractions;
using Weave.Security.Plugins;
using Weave.Security.Tokens;
using Weave.Security.Vault;
using Weave.Shared.Plugins;
using Weave.Shared.Secrets;

namespace Weave.Security.Tests;

public sealed class SecretProviderProxyTests
{
    private readonly PluginServiceBroker _broker = new(NullLogger<PluginServiceBroker>.Instance);
    private readonly CapabilityTokenService _tokenService = new();

    private CapabilityToken MintToken(string secretGrant = "secret:*") =>
        _tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws-1",
            IssuedTo = "agent-1",
            Grants = [secretGrant]
        });

    [Fact]
    public async Task ResolveAsync_NoOverride_DelegatesToFallback()
    {
        var fallback = new InMemorySecretProvider(_tokenService);
        fallback.SetSecret("db-pass", "s3cret");
        var proxy = new SecretProviderProxy(_broker, fallback);
        var token = MintToken();

        var result = await proxy.ResolveAsync("db-pass", token, TestContext.Current.CancellationToken);

        result.ToString().ShouldBe("s3cret");
    }

    [Fact]
    public async Task ResolveAsync_WithOverride_DelegatesToOverride()
    {
        var fallback = new InMemorySecretProvider(_tokenService);
        fallback.SetSecret("db-pass", "fallback-value");
        var proxy = new SecretProviderProxy(_broker, fallback);

        var mockProvider = Substitute.For<ISecretProvider>();
        mockProvider.ResolveAsync("db-pass", Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SecretValue("override-value")));
        _broker.Swap<ISecretProvider>(mockProvider);

        var token = MintToken();
        var result = await proxy.ResolveAsync("db-pass", token, TestContext.Current.CancellationToken);

        result.ToString().ShouldBe("override-value");
    }

    [Fact]
    public async Task ResolveAsync_AfterClear_RevertsToFallback()
    {
        var fallback = new InMemorySecretProvider(_tokenService);
        fallback.SetSecret("db-pass", "fallback-value");
        var proxy = new SecretProviderProxy(_broker, fallback);

        var mockProvider = Substitute.For<ISecretProvider>();
        _broker.Swap<ISecretProvider>(mockProvider);
        _broker.Swap<ISecretProvider>(null);

        var token = MintToken();
        var result = await proxy.ResolveAsync("db-pass", token, TestContext.Current.CancellationToken);

        result.ToString().ShouldBe("fallback-value");
    }

    [Fact]
    public async Task ListPathsAsync_NoOverride_DelegatesToFallback()
    {
        var fallback = new InMemorySecretProvider(_tokenService);
        fallback.SetSecret("key-1", "val");
        fallback.SetSecret("key-2", "val");
        var proxy = new SecretProviderProxy(_broker, fallback);

        var paths = await proxy.ListPathsAsync("ws-1", TestContext.Current.CancellationToken);

        paths.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ListPathsAsync_WithOverride_DelegatesToOverride()
    {
        var fallback = new InMemorySecretProvider(_tokenService);
        var proxy = new SecretProviderProxy(_broker, fallback);

        var mockProvider = Substitute.For<ISecretProvider>();
        IReadOnlyList<string> mockPaths = ["a", "b", "c"];
        mockProvider.ListPathsAsync("ws-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockPaths));
        _broker.Swap<ISecretProvider>(mockProvider);

        var paths = await proxy.ListPathsAsync("ws-1", TestContext.Current.CancellationToken);

        paths.Count.ShouldBe(3);
    }

    [Fact]
    public async Task HotSwap_OverrideToOverride_UsesLatest()
    {
        var fallback = new InMemorySecretProvider(_tokenService);
        var proxy = new SecretProviderProxy(_broker, fallback);
        var token = MintToken();

        var first = Substitute.For<ISecretProvider>();
        first.ResolveAsync("key", Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SecretValue("first")));
        _broker.Swap<ISecretProvider>(first);

        var result1 = await proxy.ResolveAsync("key", token, TestContext.Current.CancellationToken);
        result1.ToString().ShouldBe("first");

        var second = Substitute.For<ISecretProvider>();
        second.ResolveAsync("key", Arg.Any<CapabilityToken>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new SecretValue("second")));
        _broker.Swap<ISecretProvider>(second);

        var result2 = await proxy.ResolveAsync("key", token, TestContext.Current.CancellationToken);
        result2.ToString().ShouldBe("second");
    }

    [Fact]
    public async Task HotSwap_OverrideToOverrideToClear_RevertsToFallback()
    {
        var fallback = new InMemorySecretProvider(_tokenService);
        fallback.SetSecret("key", "fallback");
        var proxy = new SecretProviderProxy(_broker, fallback);
        var token = MintToken();

        _broker.Swap<ISecretProvider>(Substitute.For<ISecretProvider>());
        _broker.Swap<ISecretProvider>(Substitute.For<ISecretProvider>());
        _broker.Swap<ISecretProvider>(null);

        var result = await proxy.ResolveAsync("key", token, TestContext.Current.CancellationToken);
        result.ToString().ShouldBe("fallback");
    }
}
