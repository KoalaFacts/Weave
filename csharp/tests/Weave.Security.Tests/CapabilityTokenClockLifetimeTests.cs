using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Weave.Security.Tokens;

namespace Weave.Security.Tests;

public sealed class CapabilityTokenClockLifetimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weave-clock-lifetime-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void MintLinked_ClockReachesExpiry_RejectsAndCancelsExactlyOnce()
    {
        var service = Service(_clock);
        using var source = service.MintLinked(Request(), TestContext.Current.CancellationToken);
        var calls = 0;
        using var registration = source.Token.CancellationToken.Register(() => calls++);

        _clock.Advance(TimeSpan.FromSeconds(59));
        service.Validate(source.Token).ShouldBeTrue();
        source.Token.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        _clock.Advance(TimeSpan.FromSeconds(1));

        service.Validate(source.Token).ShouldBeFalse();
        source.Token.CancellationToken.IsCancellationRequested.ShouldBeTrue();
        calls.ShouldBe(1);
        _clock.Advance(TimeSpan.FromMinutes(5));
        calls.ShouldBe(1);
    }

    [Fact]
    public void MintLinked_IndependentClock_DoesNotCancelOtherCredentials()
    {
        var otherClock = new FakeTimeProvider(_clock.GetUtcNow());
        var first = Service(_clock);
        var second = Service(otherClock);
        using var firstSource = first.MintLinked(Request(), TestContext.Current.CancellationToken);
        using var secondSource = second.MintLinked(Request(), TestContext.Current.CancellationToken);

        _clock.Advance(TimeSpan.FromMinutes(1));

        firstSource.Token.CancellationToken.IsCancellationRequested.ShouldBeTrue();
        secondSource.Token.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        second.Validate(secondSource.Token).ShouldBeTrue();
        otherClock.Advance(TimeSpan.FromMinutes(1));
        secondSource.Token.CancellationToken.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Dispose_BeforeClockExpiry_DetachesTimerWithoutRunningCallbacks()
    {
        var service = Service(_clock);
        using var source = service.MintLinked(Request(), TestContext.Current.CancellationToken);
        var calls = 0;
        using var registration = source.Token.CancellationToken.Register(() => calls++);

        source.Dispose();
        _clock.Advance(TimeSpan.FromMinutes(2));

        calls.ShouldBe(0);
        service.Validate(source.Token).ShouldBeFalse();
        service.Revoke(source.Token.TokenId);
        calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MintLinked_ParentCancellationOrRevocation_DoesNotRepeatAtExpiry(bool revoke)
    {
        var service = Service(_clock);
        using var parent = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var source = service.MintLinked(Request(), parent.Token);
        using var unrelated = service.MintLinked(Request(), TestContext.Current.CancellationToken);
        var calls = 0;
        using var registration = source.Token.CancellationToken.Register(() => calls++);

        if (revoke)
            service.Revoke(source.Token.TokenId);
        else
            parent.Cancel();

        source.Token.CancellationToken.IsCancellationRequested.ShouldBeTrue();
        calls.ShouldBe(1);
        unrelated.Token.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        service.Validate(unrelated.Token).ShouldBeTrue();
        _clock.Advance(TimeSpan.FromMinutes(2));
        calls.ShouldBe(1);
        service.Validate(source.Token).ShouldBeFalse();
    }

    private CapabilityTokenService Service(TimeProvider clock) => new(Options.Create(new CapabilityTokenOptions
    {
        SigningKey = "test-clock-only-not-a-production-key-0123456789",
        RevocationDirectory = _root
    }), clock);

    private static CapabilityTokenRequest Request() => new()
    {
        WorkspaceId = "lifetime",
        IssuedTo = "worker",
        Grants = ["tool:files:invoke:write_file"],
        Lifetime = TimeSpan.FromMinutes(1)
    };

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
