using Weave.Actions;
using Weave.Actions.System;

namespace Weave.Actions.Tests.System;

public sealed class GetSystemInfoActionTests
{
    [Fact]
    public async Task ExecuteAsync_ReachableSilo_ReturnsSnapshotWithReachableTrue()
    {
        var snapshot = NewSnapshot();
        var configSource = Substitute.For<ISystemConfigSource>();
        configSource.Load().Returns(snapshot);

        var probe = Substitute.For<ISiloProbe>();
        probe.IsReachableAsync(Arg.Any<CancellationToken>()).Returns(true);

        var action = new GetSystemInfoAction(configSource, probe);
        var result = await action.ExecuteAsync(new GetSystemInfoInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Reachable.ShouldBeTrue();
        result.Value.BaseUrl.ShouldBe(snapshot.BaseUrl);
        result.Value.DefaultPort.ShouldBe(snapshot.DefaultPort);
        result.Value.Storage.ShouldBe(snapshot.Storage);
        result.Value.AuthMode.ShouldBe(snapshot.AuthMode);
        result.Value.RequireHttps.ShouldBe(snapshot.RequireHttps);
        result.Value.SiloPath.ShouldBe(snapshot.SiloPath);
        result.Value.WeaveHome.ShouldBe(snapshot.WeaveHome);
    }

    [Fact]
    public async Task ExecuteAsync_UnreachableSilo_StillReturnsSnapshot_WithReachableFalse()
    {
        var snapshot = NewSnapshot();
        var configSource = Substitute.For<ISystemConfigSource>();
        configSource.Load().Returns(snapshot);

        var probe = Substitute.For<ISiloProbe>();
        probe.IsReachableAsync(Arg.Any<CancellationToken>()).Returns(false);

        var action = new GetSystemInfoAction(configSource, probe);
        var result = await action.ExecuteAsync(new GetSystemInfoInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Reachable.ShouldBeFalse();
        result.Value.BaseUrl.ShouldBe(snapshot.BaseUrl);
    }

    [Fact]
    public async Task ExecuteAsync_PassesCancellationTokenToProbe()
    {
        using var cts = new CancellationTokenSource();
        var configSource = Substitute.For<ISystemConfigSource>();
        configSource.Load().Returns(NewSnapshot());
        var probe = Substitute.For<ISiloProbe>();
        probe.IsReachableAsync(cts.Token).Returns(true);

        var action = new GetSystemInfoAction(configSource, probe);
        await action.ExecuteAsync(new GetSystemInfoInput(), cts.Token);

        await probe.Received(1).IsReachableAsync(cts.Token);
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        var action = new GetSystemInfoAction(
            Substitute.For<ISystemConfigSource>(),
            Substitute.For<ISiloProbe>());

        await Should.ThrowAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    private static SystemConfigSnapshot NewSnapshot() => new()
    {
        BaseUrl = "http://localhost:9401",
        DefaultPort = 9401,
        Storage = "memory",
        AuthMode = "none",
        RequireHttps = false,
        SiloPath = null,
        WeaveHome = "/home/user/.weave"
    };
}
