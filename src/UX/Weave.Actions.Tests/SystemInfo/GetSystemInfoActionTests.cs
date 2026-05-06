using System.Net;
using Weave.Actions.SystemInfo;
using Weave.Actions.Tests.Helpers;

namespace Weave.Actions.Tests.SystemInfo;

public sealed class GetSystemInfoActionTests
{
    [Fact]
    public async Task ExecuteAsync_HealthOk_ReturnsSnapshotWithReachableTrue()
    {
        var snapshot = NewSnapshot();
        var configSource = Substitute.For<ISystemConfigSource>();
        configSource.Load().Returns(snapshot);

        using var client = HttpClientReturning(HttpStatusCode.OK);
        var action = new GetSystemInfoAction(configSource, client);

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
    public async Task ExecuteAsync_HealthNotSuccessStatus_ReturnsReachableFalse()
    {
        var configSource = Substitute.For<ISystemConfigSource>();
        configSource.Load().Returns(NewSnapshot());

        using var client = HttpClientReturning(HttpStatusCode.InternalServerError);
        var action = new GetSystemInfoAction(configSource, client);

        var result = await action.ExecuteAsync(new GetSystemInfoInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Reachable.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_HealthThrowsHttpRequestException_ReturnsReachableFalse()
    {
        var configSource = Substitute.For<ISystemConfigSource>();
        configSource.Load().Returns(NewSnapshot());

        using var client = HttpClientThrowing(new HttpRequestException("connection refused"));
        var action = new GetSystemInfoAction(configSource, client);

        var result = await action.ExecuteAsync(new GetSystemInfoInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Reachable.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ProbesHealthEndpoint()
    {
        var configSource = Substitute.For<ISystemConfigSource>();
        configSource.Load().Returns(NewSnapshot());

        var handler = StubHttpMessageHandler.Returns(HttpStatusCode.OK);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://example.test") };
        var action = new GetSystemInfoAction(configSource, client);

        await action.ExecuteAsync(new GetSystemInfoInput(), CancellationToken.None);

        handler.LastRequestUri!.AbsolutePath.ShouldBe("/health");
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        using var client = HttpClientReturning(HttpStatusCode.OK);
        var action = new GetSystemInfoAction(Substitute.For<ISystemConfigSource>(), client);

        await Should.ThrowAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    private static HttpClient HttpClientReturning(HttpStatusCode status)
        => new(StubHttpMessageHandler.Returns(status)) { BaseAddress = new Uri("http://example.test") };

    private static HttpClient HttpClientThrowing(Exception ex)
        => new(StubHttpMessageHandler.Throws(ex)) { BaseAddress = new Uri("http://example.test") };

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
