using System.Net;
using Weave.Actions.Dashboard;
using Weave.Actions.SystemInfo;
using Weave.Actions.Tests.Helpers;

namespace Weave.Actions.Tests.Dashboard;

public sealed class GetDashboardStatusActionTests
{
    [Fact]
    public async Task ExecuteAsync_NullUrl_ComputesDashboardHttpForDefaultPort()
    {
        var configSource = Substitute.For<ISystemConfigSource>();
        configSource.Load().Returns(NewSnapshot(defaultPort: 9401));
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var action = new GetDashboardStatusAction(configSource, client);

        var result = await action.ExecuteAsync(new GetDashboardStatusInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Url.ShouldBe("http://localhost:9403");
        handler.LastRequestUri!.ToString().ShouldBe("http://localhost:9403/");
    }

    [Fact]
    public async Task ExecuteAsync_NullUrl_NonDefaultPort_AddsOne()
    {
        var configSource = Substitute.For<ISystemConfigSource>();
        configSource.Load().Returns(NewSnapshot(defaultPort: 8000));
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var action = new GetDashboardStatusAction(configSource, client);

        var result = await action.ExecuteAsync(new GetDashboardStatusInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Url.ShouldBe("http://localhost:8001");
    }

    [Fact]
    public async Task ExecuteAsync_ExplicitUrl_ProbesGivenUrl()
    {
        var configSource = Substitute.For<ISystemConfigSource>();
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var action = new GetDashboardStatusAction(configSource, client);

        var result = await action.ExecuteAsync(
            new GetDashboardStatusInput("http://example.test:1234"),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Url.ShouldBe("http://example.test:1234");
        handler.LastRequestUri!.ToString().ShouldBe("http://example.test:1234/");
    }

    [Fact]
    public async Task ExecuteAsync_WhitespaceUrl_FallsBackToConfigSourceDefault()
    {
        var configSource = Substitute.For<ISystemConfigSource>();
        configSource.Load().Returns(NewSnapshot(defaultPort: 9401));
        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var action = new GetDashboardStatusAction(configSource, client);

        var result = await action.ExecuteAsync(new GetDashboardStatusInput("   "), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Url.ShouldBe("http://localhost:9403");
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.BadGateway, false)]
    public async Task ExecuteAsync_StatusCode_MapsToReachable(HttpStatusCode status, bool expected)
    {
        var configSource = Substitute.For<ISystemConfigSource>();
        configSource.Load().Returns(NewSnapshot(defaultPort: 9401));
        using var client = new HttpClient(StubHttpMessageHandler.Returns(status));
        var action = new GetDashboardStatusAction(configSource, client);

        var result = await action.ExecuteAsync(new GetDashboardStatusInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Reachable.ShouldBe(expected);
    }

    [Fact]
    public async Task ExecuteAsync_ProbeThrowsHttpRequestException_ReachableFalse()
    {
        var configSource = Substitute.For<ISystemConfigSource>();
        configSource.Load().Returns(NewSnapshot(defaultPort: 9401));
        using var client = new HttpClient(StubHttpMessageHandler.Throws(new HttpRequestException("connection refused")));
        var action = new GetDashboardStatusAction(configSource, client);

        var result = await action.ExecuteAsync(new GetDashboardStatusInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Reachable.ShouldBeFalse();
        result.Value.Url.ShouldBe("http://localhost:9403");
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        using var client = new HttpClient(StubHttpMessageHandler.Returns(HttpStatusCode.OK));
        var action = new GetDashboardStatusAction(Substitute.For<ISystemConfigSource>(), client);

        await Should.ThrowAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    private static SystemConfigSnapshot NewSnapshot(int defaultPort) => new()
    {
        Version = "1.0",
        BaseUrl = $"http://localhost:{defaultPort}",
        DefaultPort = defaultPort,
        Storage = "memory",
        AuthMode = "none",
        RequireHttps = false,
        SiloPath = null,
        WeaveHome = "/home/user/.weave"
    };
}
