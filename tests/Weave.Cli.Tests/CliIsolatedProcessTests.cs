using System.Text.Json;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class CliIsolatedProcessTests
{
    [Fact]
    public async Task ConfigSet_FreshProcesses_PersistsValuesInPrivateHomeAndPreservesOtherKeys()
    {
        using var cli = new IsolatedCliProcess();
        await cli.AssertPrivateHomeAsync();
        var siloPath = Path.Join(cli.Root, "silo directory", "Weave.Silo.dll");

        var setPath = await cli.RunAsync("config", "set", "siloPath", siloPath);
        var setPort = await cli.RunAsync("config", "set", "DEFAULTPORT", "9517");
        var readPath = await cli.RunAsync("config", "get", "siloPath");
        var readPort = await cli.RunAsync("config", "get", "defaultPort");

        setPath.ExitCode.ShouldBe(0, setPath.StandardError);
        setPath.StandardOutput.ShouldContain("siloPath =");
        setPort.ExitCode.ShouldBe(0, setPort.StandardError);
        setPort.StandardOutput.ShouldContain("defaultPort = 9517");
        readPath.ExitCode.ShouldBe(0, readPath.StandardError);
        readPath.StandardOutput.Trim().ShouldBe(siloPath);
        readPort.ExitCode.ShouldBe(0, readPort.StandardError);
        readPort.StandardOutput.Trim().ShouldBe("9517");
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(cli.ConfigPath, TestContext.Current.CancellationToken));
        config.RootElement.GetProperty("siloPath").GetString().ShouldBe(siloPath);
        config.RootElement.GetProperty("defaultPort").GetInt32().ShouldBe(9517);
    }

    [Theory]
    [InlineData("defaultPort", "65536", "'65536' is not a valid port")]
    [InlineData("unexpected-fixture-key", "value", "Unknown config key 'unexpected-fixture-key'")]
    public async Task ConfigSet_RejectedValue_ReturnsFailureAndLeavesPersistedConfigUnchanged(string key, string value, string expected)
    {
        using var cli = new IsolatedCliProcess();
        await cli.AssertPrivateHomeAsync();
        var saved = await cli.RunAsync("config", "set", "defaultPort", "9521");
        saved.ExitCode.ShouldBe(0, saved.StandardError);
        var before = await File.ReadAllTextAsync(cli.ConfigPath, TestContext.Current.CancellationToken);

        var rejected = await cli.RunAsync("config", "set", key, value);
        var readBack = await cli.RunAsync("config", "get", "defaultPort");

        rejected.ExitCode.ShouldBe(1);
        rejected.StandardOutput.ShouldContain(expected);
        readBack.ExitCode.ShouldBe(0, readBack.StandardError);
        readBack.StandardOutput.Trim().ShouldBe("9521");
        (await File.ReadAllTextAsync(cli.ConfigPath, TestContext.Current.CancellationToken)).ShouldBe(before);
    }

    [Fact]
    public async Task Version_PrivateCachedUpdate_ReadsCacheWithoutCreatingConfigOrRefreshingCache()
    {
        using var cli = new IsolatedCliProcess();
        await cli.AssertPrivateHomeAsync();
        var uncached = await cli.RunAsync("version");
        var cachePath = Path.Join(cli.WeaveHome, "update-cache.json");
        Directory.CreateDirectory(cli.WeaveHome);
        const string cache = """{"latestVersion":"999999.0.0","checkedAt":"2026-01-01T00:00:00+00:00"}""";
        await File.WriteAllTextAsync(cachePath, cache, TestContext.Current.CancellationToken);

        var cached = await cli.RunAsync("version");

        uncached.ExitCode.ShouldBe(0, uncached.StandardError);
        uncached.VisibleOutput.ShouldStartWith("weave v");
        uncached.VisibleOutput.ShouldContain("no update cache yet");
        cached.ExitCode.ShouldBe(0, cached.StandardError);
        cached.VisibleOutput.ShouldContain("v999999.0.0 is available");
        cached.VisibleOutput.ShouldContain("dotnet tool update --global Weave.Cli");
        cached.VisibleOutput.ShouldNotContain("no update cache yet");
        (await File.ReadAllTextAsync(cachePath, TestContext.Current.CancellationToken)).ShouldBe(cache);
        File.Exists(cli.ConfigPath).ShouldBeFalse();
    }
}
