using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Security.Actors;
using Weave.Security.Proxy;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Security.Vault;
using Weave.Shared.Events;

namespace Weave.Security.Tests;

public sealed class SecretProxyActorTests
{
    private static readonly CapabilityTokenService TokenService = new(
        Microsoft.Extensions.Options.Options.Create(
            new CapabilityTokenOptions { SigningKey = "test-signing-key-that-is-at-least-32-chars-long" }),
        TimeProvider.System);

    private static readonly CapabilityAuthorizer Authorizer = new(
        TokenService, Substitute.For<IEventBus>(), NullLogger<CapabilityAuthorizer>.Instance);

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
        var provider = new InMemorySecretProvider(Authorizer);
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

    // --- Register secret when secret path not found ---

    [Fact]
    public async Task RegisterSecretAsync_SecretNotInProvider_ThrowsKeyNotFound()
    {
        var (actor, _) = CreateActor();
        var token = MintToken();

        // "missing_key" was never set in the provider
        await Should.ThrowAsync<KeyNotFoundException>(
            () => actor.RegisterSecretAsync("missing_key", token));
    }

    // --- Multiple secrets registration and substitution ---

    [Fact]
    public async Task SubstituteAsync_MultipleSecrets_ReplacesAll()
    {
        var (actor, provider) = CreateActor();
        provider.SetSecret("user", "admin");
        provider.SetSecret("pass", "s3cret");
        var token = MintToken();
        await actor.RegisterSecretAsync("user", token);
        await actor.RegisterSecretAsync("pass", token);

        var result = await actor.SubstituteAsync("db://{secret:user}:{secret:pass}@host");

        result.ShouldBe("db://admin:s3cret@host");
    }

    // --- OnActivatedAsync sets key ---

    [Fact]
    public async Task OnActivatedAsync_SetsWorkspaceKey()
    {
        var (actor, provider) = CreateActor();
        provider.SetSecret("key", "val");
        var token = MintToken();

        await actor.OnActivatedAsync("my-workspace", CancellationToken.None);
        // If OnActivatedAsync sets _key, GetWorkspaceKey returns it instead of token.WorkspaceId
        // This doesn't affect observable behavior in this test, but verifying no exception
        var placeholder = await actor.RegisterSecretAsync("key", token);
        placeholder.ShouldBe("{secret:key}");
    }

    // --- Token without secret grant ---

    [Fact]
    public async Task RegisterSecretAsync_TokenWithoutSecretGrant_ThrowsUnauthorized()
    {
        var (actor, provider) = CreateActor();
        provider.SetSecret("api_key", "secret");
        var noGrantToken = TokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws-1",
            IssuedTo = "agent-1",
            Grants = ["tool:some-tool"],  // no secret:* grant
            Lifetime = TimeSpan.FromHours(1)
        });

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => actor.RegisterSecretAsync("api_key", noGrantToken));
    }

    // --- Unregister then re-register ---

    [Fact]
    public async Task UnregisterThenReRegister_UsesNewValue()
    {
        var (actor, provider) = CreateActor();
        provider.SetSecret("key", "first");
        var token = MintToken();
        await actor.RegisterSecretAsync("key", token);
        await actor.UnregisterSecretAsync("key");

        provider.SetSecret("key", "second");
        await actor.RegisterSecretAsync("key", token);

        var result = await actor.SubstituteAsync("{secret:key}");
        result.ShouldBe("second");
    }
}
