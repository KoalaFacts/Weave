using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Security.Events;
using Weave.Security.Tokens;
using Weave.Security.Vault;
using Weave.Shared.Events;

namespace Weave.Security.Tests;

/// <summary>
/// Proves <c>secret:&lt;path&gt;</c> reads now flow through
/// <see cref="ICapabilityAuthorizer"/> and publish a
/// <see cref="CapabilityAuthorizationEvent"/> on every authorize call.
/// Without this coverage the in-memory replay store never sees secret reads.
/// </summary>
public sealed class SecretProviderAuditTests
{
    private static readonly CapabilityTokenOptions _tokenOptions = new()
    {
        SigningKey = "test-signing-key-that-is-at-least-32-chars-long"
    };

    private static (InMemorySecretProvider Provider, CapabilityTokenService TokenService, CapturingEventBus Bus) CreateProvider()
    {
        var tokenService = new CapabilityTokenService(Options.Create(_tokenOptions), TimeProvider.System);
        var bus = new CapturingEventBus();
        var authorizer = new CapabilityAuthorizer(tokenService, bus, NullLogger<CapabilityAuthorizer>.Instance);
        return (new InMemorySecretProvider(authorizer), tokenService, bus);
    }

    private static CapabilityToken Mint(CapabilityTokenService svc, string grant) =>
        svc.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = "ws-1",
            IssuedTo = "agent-1",
            Grants = [grant],
            Lifetime = TimeSpan.FromHours(1)
        });

    [Fact]
    public async Task ResolveAsync_AllowsRead_PublishesAllowEvent()
    {
        var (provider, tokenService, bus) = CreateProvider();
        provider.SetSecret("db-pass", "v");
        var token = Mint(tokenService, "secret:db-pass");

        await provider.ResolveAsync("db-pass", token, TestContext.Current.CancellationToken);

        bus.Events.Count.ShouldBe(1);
        var evt = bus.Events[0];
        evt.Outcome.ShouldBe(CapabilityAuthorizationOutcome.Allow);
        evt.Grant.ShouldBe("secret:db-pass");
        evt.WorkspaceId.ShouldBe("ws-1");
        evt.IssuedTo.ShouldBe("agent-1");
        evt.TokenId.ShouldBe(token.TokenId);
        evt.Reason.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_WildcardGrant_PublishesAllowEventWithSpecificGrant()
    {
        var (provider, tokenService, bus) = CreateProvider();
        provider.SetSecret("api-key", "v");
        var token = Mint(tokenService, "secret:*");

        await provider.ResolveAsync("api-key", token, TestContext.Current.CancellationToken);

        bus.Events.Count.ShouldBe(1);
        // Audit row records the requested grant, not the wildcard owned grant.
        bus.Events[0].Grant.ShouldBe("secret:api-key");
        bus.Events[0].Outcome.ShouldBe(CapabilityAuthorizationOutcome.Allow);
    }

    [Fact]
    public async Task ResolveAsync_MissingGrant_PublishesDenyEventThenThrows()
    {
        var (provider, tokenService, bus) = CreateProvider();
        var token = Mint(tokenService, "secret:other");

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => provider.ResolveAsync("forbidden", token, TestContext.Current.CancellationToken));

        bus.Events.Count.ShouldBe(1);
        var evt = bus.Events[0];
        evt.Outcome.ShouldBe(CapabilityAuthorizationOutcome.Deny);
        evt.Grant.ShouldBe("secret:forbidden");
        evt.Reason.ShouldBe("grant-missing");
    }

    private sealed class CapturingEventBus : IEventBus
    {
        public List<CapabilityAuthorizationEvent> Events { get; } = [];

        public Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct) where TEvent : IDomainEvent
        {
            if (domainEvent is CapabilityAuthorizationEvent capabilityEvent)
                Events.Add(capabilityEvent);
            return Task.CompletedTask;
        }

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IDomainEvent =>
            throw new NotSupportedException();
    }
}
