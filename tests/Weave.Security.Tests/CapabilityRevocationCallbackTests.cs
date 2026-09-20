using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Weave.Security.Tokens;

namespace Weave.Security.Tests;

public sealed class CapabilityRevocationCallbackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weave-revocation-callbacks-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero));
    private string Store => Path.Combine(_root, "revocations");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Revoke_WriteAndCallbacksFail_PreservesEveryCauseAndAllowsExplicitDurableRetry(bool replacedByFile)
    {
        var service = Service();
        using var linked = service.MintLinked(Request(), TestContext.Current.CancellationToken);
        var first = new InvalidOperationException("first callback failed");
        var second = new ObjectDisposedException("callback-owned-object");
        var healthyCalls = 0;
        using var healthy = linked.Token.CancellationToken.Register(() => healthyCalls++);
        using var one = linked.Token.CancellationToken.Register(() => throw first);
        using var two = linked.Token.CancellationToken.Register(() => throw second);
        var saved = Path.Combine(_root, "retained");
        Directory.Move(Store, saved);
        if (replacedByFile)
            File.WriteAllText(Store, "not a directory");

        var failure = Should.Throw<AggregateException>(() => service.Revoke(linked.Token.TokenId));

        var causes = failure.Flatten().InnerExceptions;
        causes.Count.ShouldBe(3);
        causes.ShouldContain(first);
        causes.ShouldContain(second);
        causes.Any(e => e is IOException or UnauthorizedAccessException).ShouldBeTrue();
        linked.Token.CancellationToken.IsCancellationRequested.ShouldBeTrue();
        healthyCalls.ShouldBe(1);
        service.Validate(linked.Token).ShouldBeFalse();
        File.Exists(Path.Combine(saved, linked.Token.TokenId + ".revoked")).ShouldBeFalse();

        if (replacedByFile)
            File.Delete(Store);
        Directory.Move(saved, Store);
        var verifier = Service();
        // Local cancellation is not evidence of persistence for another verifier.
        verifier.Validate(linked.Token).ShouldBeTrue();
        service.Revoke(linked.Token.TokenId);
        healthyCalls.ShouldBe(1);
        verifier.Validate(linked.Token).ShouldBeFalse();
        Service().Validate(linked.Token).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Revoke_CallbackFailsAfterSuccessfulWrite_PreservesDurableDenialAndOtherTokens(bool disposedCallback)
    {
        var service = Service();
        var verifier = Service();
        using var linked = service.MintLinked(Request(), TestContext.Current.CancellationToken);
        using var other = service.MintLinked(Request(), TestContext.Current.CancellationToken);
        Exception expected = disposedCallback ? new ObjectDisposedException("callback-owned-object")
            : new InvalidOperationException("callback failure");
        var healthyCalls = 0;
        using var healthy = linked.Token.CancellationToken.Register(() => healthyCalls++);
        using var throwing = linked.Token.CancellationToken.Register(() => throw expected);

        var failure = Should.Throw<AggregateException>(() => service.Revoke(linked.Token.TokenId));

        var causes = failure.Flatten().InnerExceptions;
        causes.Count.ShouldBe(1);
        causes.ShouldContain(expected);
        healthyCalls.ShouldBe(1);
        linked.Token.CancellationToken.IsCancellationRequested.ShouldBeTrue();
        verifier.Validate(linked.Token).ShouldBeFalse();
        Service().Validate(linked.Token).ShouldBeFalse();
        other.Token.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        verifier.Validate(other.Token).ShouldBeTrue();
        service.Revoke(linked.Token.TokenId);
        healthyCalls.ShouldBe(1);
    }

    [Fact]
    public void Revoke_DisposedLinkedSource_StillPersistsWithoutRunningItsCallbacks()
    {
        var service = Service();
        var verifier = Service();
        using var linked = service.MintLinked(Request(), TestContext.Current.CancellationToken);
        var calls = 0;
        using var registration = linked.Token.CancellationToken.Register(() => calls++);
        linked.Dispose();

        service.Revoke(linked.Token.TokenId);

        calls.ShouldBe(0);
        verifier.Validate(linked.Token).ShouldBeFalse();
        Service().Validate(linked.Token).ShouldBeFalse();
    }

    [Fact]
    public void Revoke_ParentAlreadyCancelled_PersistsWithoutRepeatingCallbacks()
    {
        var service = Service();
        var verifier = Service();
        using var parent = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var linked = service.MintLinked(Request(), parent.Token);
        var calls = 0;
        using var registration = linked.Token.CancellationToken.Register(() => calls++);
        parent.Cancel();
        calls.ShouldBe(1);

        service.Revoke(linked.Token.TokenId);

        calls.ShouldBe(1);
        verifier.Validate(linked.Token).ShouldBeFalse();
        Service().Validate(linked.Token).ShouldBeFalse();
    }

    private CapabilityTokenService Service() => new(Options.Create(new CapabilityTokenOptions
    {
        SigningKey = "test-revocation-callback-key-not-production-0123456789",
        RevocationDirectory = Store
    }), _clock);

    private static CapabilityTokenRequest Request() => new()
    {
        WorkspaceId = "workspace",
        IssuedTo = "agent",
        Grants = ["tool:files:invoke:write_file"],
        Lifetime = TimeSpan.FromHours(1)
    };

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
