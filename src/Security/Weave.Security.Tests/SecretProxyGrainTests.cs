using Microsoft.Extensions.Logging;
using Weave.Security.Grains;
using Weave.Security.Proxy;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Security.Vault;

namespace Weave.Security.Tests;

public sealed class SecretProxyGrainTests
{
    private static readonly CapabilityTokenService TokenService = new(
        Microsoft.Extensions.Options.Options.Create(
            new CapabilityTokenOptions { SigningKey = "test-signing-key-that-is-at-least-32-chars-long" }));

    private static CapabilityToken MintToken() =>
        TokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws-1",
            IssuedTo = "agent-1",
            Grants = ["secret:*"],
            Lifetime = TimeSpan.FromHours(1)
        });

    private static (SecretProxyGrain Grain, InMemorySecretProvider Provider) CreateGrain()
    {
        var provider = new InMemorySecretProvider(TokenService);
        var scanner = new LeakScanner(Substitute.For<ILogger<LeakScanner>>());
        var proxy = new TransparentSecretProxy(scanner, Substitute.For<ILogger<TransparentSecretProxy>>());
        var grain = new SecretProxyGrain(proxy, provider, Substitute.For<ILogger<SecretProxyGrain>>());
        return (grain, provider);
    }

    [Fact]
    public async Task RegisterSecretAsync_ResolvesAndRegisters()
    {
        var (grain, provider) = CreateGrain();
        provider.SetSecret("api_key", "super-secret-value");
        var token = MintToken();

        var placeholder = await grain.RegisterSecretAsync("api_key", token);

        placeholder.ShouldBe("{secret:api_key}");
    }

    [Fact]
    public async Task SubstituteAsync_AfterRegister_ReplacesPlaceholder()
    {
        var (grain, provider) = CreateGrain();
        provider.SetSecret("api_key", "super-secret-value");
        var token = MintToken();
        await grain.RegisterSecretAsync("api_key", token);

        var result = await grain.SubstituteAsync("Authorization: {secret:api_key}");

        result.ShouldBe("Authorization: super-secret-value");
    }

    [Fact]
    public async Task UnregisterSecretAsync_RemovesMapping()
    {
        var (grain, provider) = CreateGrain();
        provider.SetSecret("api_key", "secret");
        var token = MintToken();
        await grain.RegisterSecretAsync("api_key", token);

        await grain.UnregisterSecretAsync("api_key");

        var result = await grain.SubstituteAsync("{secret:api_key}");
        result.ShouldBe("{secret:api_key}");
    }

    [Fact]
    public async Task RegisterSecretAsync_WithInvalidToken_Throws()
    {
        var (grain, provider) = CreateGrain();
        provider.SetSecret("api_key", "secret");
        var expiredToken = TokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws-1",
            IssuedTo = "agent",
            Grants = ["secret:*"],
            Lifetime = TimeSpan.FromMilliseconds(-1)
        });

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => grain.RegisterSecretAsync("api_key", expiredToken));
    }

    [Fact]
    public async Task SubstituteAsync_WithNoRegisteredSecrets_ReturnsOriginal()
    {
        var (grain, _) = CreateGrain();

        var result = await grain.SubstituteAsync("plain text with no placeholders");

        result.ShouldBe("plain text with no placeholders");
    }
}
