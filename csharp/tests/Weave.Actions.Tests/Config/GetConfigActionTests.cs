using Weave.Actions.Config;
using Weave.Actions.Context;
using Weave.Actions.SystemInfo;

namespace Weave.Actions.Tests.Config;

public sealed class GetConfigActionTests
{
    [Fact]
    public async Task ExecuteAsync_NoKey_ReturnsFullSummaryWithNoRequestedValue()
    {
        var configSource = Substitute.For<ISystemConfigSource>();
        configSource.Load().Returns(NewSnapshot());
        var action = new GetConfigAction(configSource);

        var result = await action.ExecuteAsync(new GetConfigInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.RequestedValue.ShouldBeNull();
        result.Value.Summary.Version.ShouldBe("1.0");
        result.Value.Summary.DefaultPort.ShouldBe("9401");
        result.Value.Summary.Storage.ShouldBe("memory");
        result.Value.Summary.AuthMode.ShouldBe("none");
        result.Value.Summary.RequireHttps.ShouldBe("false");
        result.Value.Summary.SiloPath.ShouldBe("(not set)");
        result.Value.Summary.WeaveHome.ShouldBe("/home/user/.weave");
        result.Value.Summary.BaseUrl.ShouldBe("http://localhost:9401");
    }

    [Fact]
    public async Task ExecuteAsync_NullSiloPath_RendersAsNotSet()
    {
        var configSource = Substitute.For<ISystemConfigSource>();
        configSource.Load().Returns(NewSnapshot() with { SiloPath = null });
        var action = new GetConfigAction(configSource);

        var result = await action.ExecuteAsync(new GetConfigInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Summary.SiloPath.ShouldBe("(not set)");
    }

    [Fact]
    public async Task ExecuteAsync_RequireHttpsTrue_RendersAsLowercaseString()
    {
        var configSource = Substitute.For<ISystemConfigSource>();
        configSource.Load().Returns(NewSnapshot() with { RequireHttps = true });
        var action = new GetConfigAction(configSource);

        var result = await action.ExecuteAsync(new GetConfigInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Summary.RequireHttps.ShouldBe("true");
    }

    [Theory]
    [InlineData("version", "1.0")]
    [InlineData("VERSION", "1.0")]
    [InlineData("defaultPort", "9401")]
    [InlineData("DEFAULTPORT", "9401")]
    [InlineData("storage", "memory")]
    [InlineData("authMode", "none")]
    [InlineData("requireHttps", "false")]
    [InlineData("siloPath", "(not set)")]
    [InlineData("weaveHome", "/home/user/.weave")]
    [InlineData("baseUrl", "http://localhost:9401")]
    public async Task ExecuteAsync_KnownKey_ReturnsRequestedValue(string key, string expected)
    {
        var configSource = Substitute.For<ISystemConfigSource>();
        configSource.Load().Returns(NewSnapshot());
        var action = new GetConfigAction(configSource);

        var result = await action.ExecuteAsync(new GetConfigInput(key), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.RequestedValue.ShouldBe(expected);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownKey_ReturnsValidationFailedWithKeyHint()
    {
        var configSource = Substitute.For<ISystemConfigSource>();
        configSource.Load().Returns(NewSnapshot());
        var action = new GetConfigAction(configSource);

        var result = await action.ExecuteAsync(new GetConfigInput("nope"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure!.Reason.ShouldBe(ActionFailureReason.ValidationFailed);
        result.Failure.Message.ShouldContain("nope");
        result.Failure.Message.ShouldContain("version");
        result.Failure.Message.ShouldContain("baseUrl");
    }

    [Fact]
    public async Task ExecuteAsync_CancelledToken_Throws()
    {
        var configSource = Substitute.For<ISystemConfigSource>();
        configSource.Load().Returns(NewSnapshot());
        var action = new GetConfigAction(configSource);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => action.ExecuteAsync(new GetConfigInput(), cts.Token));
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        var action = new GetConfigAction(Substitute.For<ISystemConfigSource>());

        await Should.ThrowAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    private static SystemConfigSnapshot NewSnapshot() => new()
    {
        Version = "1.0",
        BaseUrl = "http://localhost:9401",
        DefaultPort = 9401,
        Storage = "memory",
        AuthMode = "none",
        RequireHttps = false,
        SiloPath = null,
        WeaveHome = "/home/user/.weave"
    };
}
