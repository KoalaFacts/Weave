using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Security.Events;
using Weave.Security.Tokens;
using Weave.Shared.Events;

namespace Weave.Security.Tests;

public sealed class CapabilityAuthorizerTests
{
    private const string TestWorkspace = "ws-1";

    private static CapabilityTokenService CreateTokenService() =>
        new(
            Options.Create(new CapabilityTokenOptions { SigningKey = "test-signing-key-that-is-at-least-32-chars-long" }),
            TimeProvider.System);

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

    private static (CapabilityAuthorizer Authorizer, CapabilityTokenService TokenService, CapturingEventBus Bus) CreateAuthorizer()
    {
        var tokenService = CreateTokenService();
        var bus = new CapturingEventBus();
        var authorizer = new CapabilityAuthorizer(tokenService, bus, NullLogger<CapabilityAuthorizer>.Instance);
        return (authorizer, tokenService, bus);
    }

    private static CapabilityToken Mint(
        CapabilityTokenService svc,
        IEnumerable<string> grants,
        string workspaceId = TestWorkspace,
        string issuedTo = "agent-1",
        TimeSpan? lifetime = null) =>
        svc.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = workspaceId,
            IssuedTo = issuedTo,
            Grants = [.. grants],
            Lifetime = lifetime ?? TimeSpan.FromHours(1)
        });

    [Fact]
    public async Task AuthorizeAsync_WithValidTokenAndGrant_DoesNotThrow()
    {
        var (authorizer, svc, _) = CreateAuthorizer();
        var token = Mint(svc, ["tool:git"]);

        await authorizer.AuthorizeAsync(token, "tool:git", TestWorkspace, "Test.Allow");
    }

    [Fact]
    public async Task AuthorizeAsync_WithValidTokenAndGrant_PublishesAllowEvent()
    {
        var (authorizer, svc, bus) = CreateAuthorizer();
        var token = Mint(svc, ["tool:git"]);

        await authorizer.AuthorizeAsync(token, "tool:git", TestWorkspace, "Test.Allow");

        bus.Events.Count.ShouldBe(1);
        var evt = bus.Events[0];
        evt.Outcome.ShouldBe(CapabilityAuthorizationOutcome.Allow);
        evt.Reason.ShouldBeNull();
        evt.TokenId.ShouldBe(token.TokenId);
        evt.Grant.ShouldBe("tool:git");
        evt.IssuedTo.ShouldBe("agent-1");
        evt.WorkspaceId.ShouldBe(TestWorkspace);
        evt.ActionContext.ShouldBe("Test.Allow");
        evt.SourceId.ShouldBe($"{TestWorkspace}/{token.TokenId}");
    }

    [Fact]
    public async Task AuthorizeAsync_WithInvalidToken_Throws()
    {
        var (authorizer, svc, _) = CreateAuthorizer();
        var expired = Mint(svc, ["tool:git"], lifetime: TimeSpan.FromMilliseconds(-1));

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => authorizer.AuthorizeAsync(expired, "tool:git", TestWorkspace, "Test.InvalidToken"));
    }

    [Fact]
    public async Task AuthorizeAsync_WithInvalidToken_PublishesDenyWithReason()
    {
        var (authorizer, svc, bus) = CreateAuthorizer();
        var expired = Mint(svc, ["tool:git"], lifetime: TimeSpan.FromMilliseconds(-1));

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => authorizer.AuthorizeAsync(expired, "tool:git", TestWorkspace, "Test.InvalidToken"));

        bus.Events.Count.ShouldBe(1);
        bus.Events[0].Outcome.ShouldBe(CapabilityAuthorizationOutcome.Deny);
        bus.Events[0].Reason.ShouldBe("invalid-or-expired-token");
        bus.Events[0].ActionContext.ShouldBe("Test.InvalidToken");
    }

    [Fact]
    public async Task AuthorizeAsync_WithMismatchedWorkspace_PublishesDenyWithReason()
    {
        var (authorizer, svc, bus) = CreateAuthorizer();
        var token = Mint(svc, ["tool:git"], workspaceId: "other-ws");

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => authorizer.AuthorizeAsync(token, "tool:git", TestWorkspace, "Test.WorkspaceMismatch"));

        bus.Events.Count.ShouldBe(1);
        bus.Events[0].Outcome.ShouldBe(CapabilityAuthorizationOutcome.Deny);
        bus.Events[0].Reason.ShouldBe("workspace-mismatch");
        bus.Events[0].WorkspaceId.ShouldBe("other-ws");
    }

    [Fact]
    public async Task AuthorizeAsync_WithNullActorWorkspace_SkipsWorkspaceCheck()
    {
        var (authorizer, svc, bus) = CreateAuthorizer();
        var token = Mint(svc, ["plugin:invoke:dapr"], workspaceId: "system");

        await authorizer.AuthorizeAsync(token, "plugin:invoke:dapr", actorWorkspaceId: null, "Test.NullWorkspace");

        bus.Events.Count.ShouldBe(1);
        bus.Events[0].Outcome.ShouldBe(CapabilityAuthorizationOutcome.Allow);
        bus.Events[0].WorkspaceId.ShouldBe("system");
    }

    [Fact]
    public async Task AuthorizeAsync_WithEmptyActorWorkspace_SkipsWorkspaceCheck()
    {
        var (authorizer, svc, _) = CreateAuthorizer();
        var token = Mint(svc, ["plugin:invoke:dapr"], workspaceId: "system");

        await authorizer.AuthorizeAsync(token, "plugin:invoke:dapr", actorWorkspaceId: "", "Test.EmptyWorkspace");
    }

    [Fact]
    public async Task AuthorizeAsync_WithMissingGrant_PublishesDenyWithReason()
    {
        var (authorizer, svc, bus) = CreateAuthorizer();
        var token = Mint(svc, ["tool:other"]);

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => authorizer.AuthorizeAsync(token, "tool:git", TestWorkspace, "Test.GrantMissing"));

        bus.Events.Count.ShouldBe(1);
        bus.Events[0].Outcome.ShouldBe(CapabilityAuthorizationOutcome.Deny);
        bus.Events[0].Reason.ShouldBe("grant-missing");
        bus.Events[0].Grant.ShouldBe("tool:git");
    }

    [Fact]
    public async Task AuthorizeAsync_DefaultsActionContextToCallerMemberName()
    {
        var (authorizer, svc, bus) = CreateAuthorizer();
        var token = Mint(svc, ["tool:git"]);

        await authorizer.AuthorizeAsync(token, "tool:git", TestWorkspace);

        bus.Events.Count.ShouldBe(1);
        bus.Events[0].ActionContext.ShouldBe(nameof(AuthorizeAsync_DefaultsActionContextToCallerMemberName));
    }

    [Fact]
    public async Task AuthorizeAsync_OnDeny_ChecksHappenInOrder_InvalidTokenBeforeWorkspaceBeforeGrant()
    {
        var (authorizer, svc, bus) = CreateAuthorizer();
        // Expired AND wrong workspace AND missing grant — invalid-or-expired-token reported first.
        var token = Mint(svc, ["tool:other"], workspaceId: "other-ws", lifetime: TimeSpan.FromMilliseconds(-1));

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => authorizer.AuthorizeAsync(token, "tool:git", TestWorkspace, "Test.Order"));

        bus.Events[0].Reason.ShouldBe("invalid-or-expired-token");
    }

    [Fact]
    public async Task AuthorizeAsync_OnAllow_SourceIdComposesWorkspaceAndToken()
    {
        var (authorizer, svc, bus) = CreateAuthorizer();
        var token = Mint(svc, ["tool:git"], workspaceId: "ws-x");

        await authorizer.AuthorizeAsync(token, "tool:git", "ws-x", "Test.SourceId");

        bus.Events[0].SourceId.ShouldBe($"ws-x/{token.TokenId}");
    }

    [Fact]
    public async Task AuthorizeAsync_WithValidGrantButMismatchedWorkspace_PublishesDenyWithWorkspaceReason()
    {
        // Defends against an order-of-checks regression: the workspace check
        // must run BEFORE the grant check. Without this test, a future refactor
        // that flipped the order would still pass AuthorizeAsync_WithMismatchedWorkspace_*
        // because that fixture happens to mint with a non-matching grant.
        var (authorizer, svc, bus) = CreateAuthorizer();
        var token = Mint(svc, ["tool:git"], workspaceId: "other-ws");

        await Should.ThrowAsync<UnauthorizedAccessException>(
            () => authorizer.AuthorizeAsync(token, "tool:git", TestWorkspace, "Test.WorkspaceBeforeGrant"));

        bus.Events.Count.ShouldBe(1);
        bus.Events[0].Reason.ShouldBe("workspace-mismatch");
    }

    [Fact]
    public async Task AuthorizeAsync_TruncatesActionContextToMaxLength()
    {
        var (authorizer, svc, bus) = CreateAuthorizer();
        var token = Mint(svc, ["tool:git"]);
        var oversized = new string('x', CapabilityAuthorizer.MaxActionContextLength + 50);

        await authorizer.AuthorizeAsync(token, "tool:git", TestWorkspace, oversized);

        bus.Events[0].ActionContext.Length.ShouldBe(CapabilityAuthorizer.MaxActionContextLength);
    }

    [Fact]
    public async Task AuthorizeAsync_StripsControlCharsFromActionContext()
    {
        var (authorizer, svc, bus) = CreateAuthorizer();
        var token = Mint(svc, ["tool:git"]);

        await authorizer.AuthorizeAsync(token, "tool:git", TestWorkspace, "evil\n\u001b[31mctx\t!");

        bus.Events[0].ActionContext.ShouldNotContain('\n');
        bus.Events[0].ActionContext.ShouldNotContain('\t');
        bus.Events[0].ActionContext.ShouldNotContain('\u001b');
        bus.Events[0].ActionContext.ShouldContain("ctx");
        bus.Events[0].ActionContext.ShouldContain("!");
    }

    [Fact]
    public async Task AuthorizeAsync_PassesTokenCancellationTokenToEventBus()
    {
        var tokenService = CreateTokenService();
        using var parentCts = new CancellationTokenSource();
        using var source = tokenService.MintLinked(new CapabilityTokenRequest
        {
            WorkspaceId = TestWorkspace,
            IssuedTo = "agent-1",
            Grants = ["tool:git"],
            Lifetime = TimeSpan.FromHours(1)
        }, parentCts.Token);

        var bus = new CtCapturingEventBus();
        var authorizer = new CapabilityAuthorizer(tokenService, bus, NullLogger<CapabilityAuthorizer>.Instance);

        await authorizer.AuthorizeAsync(source.Token, "tool:git", TestWorkspace, "Test.Ct");

        bus.LastCancellationToken.ShouldBe(source.Token.CancellationToken);
    }

    private sealed class CtCapturingEventBus : IEventBus
    {
        public CancellationToken LastCancellationToken { get; private set; }

        public Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct) where TEvent : IDomainEvent
        {
            LastCancellationToken = ct;
            return Task.CompletedTask;
        }

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler) where TEvent : IDomainEvent =>
            throw new NotSupportedException();
    }
}
