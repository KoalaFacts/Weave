using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Security.Events;
using Weave.Security.Tokens;
using Weave.Shared.Events;

namespace Weave.Security.Tests;

public sealed class PrincipalKindTests
{
    private const string Workspace = "ws-1";

    private readonly CapabilityTokenService _tokens = new(
        Options.Create(new CapabilityTokenOptions { SigningKey = "test-signing-key-that-is-at-least-32-chars-long" }),
        TimeProvider.System);

    private CapabilityToken Mint(PrincipalKind kind, params string[] grants) => _tokens.Mint(new CapabilityTokenRequest
    {
        WorkspaceId = Workspace,
        IssuedTo = kind == PrincipalKind.Human ? "reviewer" : "agent-1",
        Kind = kind,
        Grants = [.. grants],
        Lifetime = TimeSpan.FromHours(1)
    });

    private CapabilityAuthorizer Authorizer(CapturingEventBus? bus = null) =>
        new(_tokens, bus ?? new CapturingEventBus(), NullLogger<CapabilityAuthorizer>.Instance);

    [Fact]
    public void Mint_DefaultRequest_IssuesAgentPrincipal()
    {
        Mint(PrincipalKind.Agent, "tool:git").Kind.ShouldBe(PrincipalKind.Agent);
        new CapabilityTokenRequest().Kind.ShouldBe(PrincipalKind.Agent);
    }

    [Fact]
    public void Mint_ApprovalGrantForAgent_Throws()
    {
        Should.Throw<ArgumentException>(() => Mint(PrincipalKind.Agent, "invocation:read", "tool:files:approve:write_file"));
    }

    [Fact]
    public void Mint_ApprovalGrantForHuman_Succeeds()
    {
        var token = Mint(PrincipalKind.Human, "tool:files:approve:write_file");
        token.Kind.ShouldBe(PrincipalKind.Human);
        _tokens.Validate(token).ShouldBeTrue();
    }

    [Theory]
    [InlineData(PrincipalKind.Agent)]
    [InlineData(PrincipalKind.Human)]
    public void Mint_ReservedPrincipalGrant_Throws(PrincipalKind kind)
    {
        Should.Throw<ArgumentException>(() => Mint(kind, PrincipalRequirement.Human));
    }

    [Fact]
    public void Mint_UndefinedKind_Throws()
    {
        Should.Throw<ArgumentException>(() => Mint((PrincipalKind)7, "tool:git"));
    }

    [Fact]
    public void Validate_AgentTokenRelabelledHuman_ReturnsFalse()
    {
        var token = Mint(PrincipalKind.Agent, "*");
        _tokens.Validate(token with { Kind = PrincipalKind.Human }).ShouldBeFalse();
    }

    [Fact]
    public async Task AuthorizeAsync_HumanRequirementWithAgentWildcardToken_ThrowsAndAuditsReason()
    {
        var bus = new CapturingEventBus();
        var token = Mint(PrincipalKind.Agent, "*");

        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            Authorizer(bus).AuthorizeAsync(token, PrincipalRequirement.Human, Workspace));

        var denied = bus.Events.ShouldHaveSingleItem();
        denied.Outcome.ShouldBe(CapabilityAuthorizationOutcome.Deny);
        denied.Reason.ShouldBe("human-principal-required");
    }

    [Fact]
    public async Task AuthorizeAsync_HumanRequirementWithHumanToken_Allows()
    {
        var token = Mint(PrincipalKind.Human, "invocation:read");
        await Authorizer().AuthorizeAsync(token, PrincipalRequirement.Human, Workspace);
    }

    [Fact]
    public async Task AuthorizeAsync_HumanRequirementWithRelabelledToken_ThrowsInvalidToken()
    {
        var token = Mint(PrincipalKind.Agent, "invocation:read") with { Kind = PrincipalKind.Human };
        var error = await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            Authorizer().AuthorizeAsync(token, PrincipalRequirement.Human, Workspace));
        error.Message.ShouldContain("Invalid or expired");
    }

    [Fact]
    public async Task AuthorizeAsync_HumanRequirementFromAnotherWorkspace_Throws()
    {
        var token = Mint(PrincipalKind.Human, "invocation:read");
        await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            Authorizer().AuthorizeAsync(token, PrincipalRequirement.Human, "ws-2"));
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
