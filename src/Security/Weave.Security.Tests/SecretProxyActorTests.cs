using Microsoft.Extensions.Logging;
using Weave.Security.Actors;
using Weave.Security.Proxy;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Security.Vault;

namespace Weave.Security.Tests;

public sealed class SecretProxyActorTests
{
    private static readonly CapabilityTokenService TokenService = new(
        Microsoft.Extensions.Options.Options.Create(
            new CapabilityTokenOptions { SigningKey = "test-signing-key-that-is-at-least-32-chars-long" }),
        TimeProvider.System);

    private static CapabilityToken MintToken() =>
        TokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws-1",
            IssuedTo = "agent-1",
            Grants = ["secret:*"],
            Lifetime = TimeSpan.FromHours(1)
        });

    private static (SecretProxyActor Actor, InMemorySecretProvider Provider) CreateActor()
    {
        var provider = new InMemorySecretProvider(TokenService);
        var scanner = new LeakScanner(Substitute.For<ILogger<LeakScanner>>());
        var proxy = new TransparentSecretProxy(scanner, Substitute.For<ILogger<TransparentSecretProxy>>());
        var actor = new SecretProxyActor(proxy, provider, Substitute.For<ILogger<SecretProxyActor>>());
        return (actor, provider);
    }

    [Fact]
    public async Task RegisterSecretAsync_ResolvesAndRegisters()
    {
        var (actor, provider) = CreateActor();
        provider.SetSecret("api_key", "super-secret-value");
        var token = MintToken();

        var placeholder = await actor.RegisterSecretAsync("api_key", token);

        placeholder.ShouldBe("{secret:api_key}");
    }

    [Fact]
    public async Task SubstituteAsync_AfterRegister_ReplacesPlaceholder()
    {
        var (actor, provider) = CreateActor();
        provider.SetSecret("api_key", "super-secret-value");
        var token = MintToken();
        await actor.RegisterSecretAsync("api_key", token);

        var result = await actor.SubstituteAsync("Authorization: {secret:api_key}");

        result.ShouldBe("Authorization: super-secret-value");
    }

    [Fact]
    public async Task UnregisterSecretAsync_RemovesMapping()
    {
        var (actor, provider) = CreateActor();
        provider.SetSecret("api_key", "secret");
        var token = MintToken();
        await actor.RegisterSecretAsync("api_key", token);

        await actor.UnregisterSecretAsync("api_key");

        var result = await actor.SubstituteAsync("{secret:api_key}");
        result.ShouldBe("{secret:api_key}");
    }

    [Fact]
    public async Task RegisterSecretAsync_WithInvalidToken_Throws()
    {
        var (actor, provider) = CreateActor();
        provider.SetSecret("api_key", "secret");
        var expiredToken = TokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws-1",
            IssuedTo = "agent",
            Grants = ["secret:*"],
            Lifetime = TimeSpan.FromMilliseconds(-1)
        });

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => actor.RegisterSecretAsync("api_key", expiredToken));
    }

    [Fact]
    public async Task SubstituteAsync_WithNoRegisteredSecrets_ReturnsOriginal()
    {
        var (actor, _) = CreateActor();

        var result = await actor.SubstituteAsync("plain text with no placeholders");

        result.ShouldBe("plain text with no placeholders");
    }
}
