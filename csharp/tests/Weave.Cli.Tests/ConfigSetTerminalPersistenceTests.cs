using System.Text;

namespace Weave.Cli.Tests;

[Collection("CLI init PTY")]
[Trait("Category", "Integration")]
public sealed class ConfigSetTerminalPersistenceTests
{
    private const string ExistingConfig = """
        {
          "version": "1.0",
          "siloPath": "/fixture/original-runtime",
          "defaultPort": 9511,
          "storage": "memory",
          "authMode": "none",
          "requireHttps": false,
          "preserveMarker": "original bytes including unknown field"
        }
        """;

    [Fact]
    public async Task ConfigSet_NoArguments_SelectsPortAndPersistsTypedValue()
    {
        using var fixture = new InitWizardProcessFixture();
        await File.WriteAllTextAsync(fixture.ConfigPath, ExistingConfig, TestContext.Current.CancellationToken);

        using var result = await fixture.RunAsync("config-menu");

        using var config = fixture.ReadConfig();
        config.RootElement.GetProperty("defaultPort").GetInt32().ShouldBe(9529);
        config.RootElement.GetProperty("siloPath").GetString().ShouldBe("/fixture/original-runtime");
        config.RootElement.GetProperty("storage").GetString().ShouldBe("memory");
        var output = result.RootElement.GetProperty("output").GetString().ShouldNotBeNull();
        output.ShouldContain("Which config value would you like to update?");
        output.ShouldContain("defaultPort = 9529");
        result.RootElement.GetProperty("stages").EnumerateArray().Select(stage => stage.GetString())
            .ShouldBe(["port-selected", "port-persisted"]);
    }

    [Fact]
    public async Task ConfigSet_ExplicitMixedCaseKey_PromptsOnlyForValueAndPersistsSpacedPath()
    {
        using var fixture = new InitWizardProcessFixture();
        await File.WriteAllTextAsync(fixture.ConfigPath, ExistingConfig, TestContext.Current.CancellationToken);

        using var result = await fixture.RunAsync("config-path");

        using var config = fixture.ReadConfig();
        config.RootElement.GetProperty("siloPath").GetString().ShouldBe(fixture.ExplicitRuntime);
        config.RootElement.GetProperty("defaultPort").GetInt32().ShouldBe(9511);
        var output = result.RootElement.GetProperty("output").GetString().ShouldNotBeNull();
        output.ShouldContain("SiLoPaTh:");
        output.ShouldNotContain("Which config value would you like to update?");
        output.ShouldContain($"siloPath = {fixture.ExplicitRuntime}");
        (await File.ReadAllTextAsync(fixture.ExplicitRuntime, TestContext.Current.CancellationToken))
            .ShouldBe("fixture runtime path; init must not execute this file");
    }

    [Fact]
    public async Task ConfigSet_PromptedPortAboveMaximum_ReturnsFailureAndPreservesOriginalBytes()
    {
        using var fixture = new InitWizardProcessFixture();
        var original = Encoding.UTF8.GetBytes(ExistingConfig + "\n");
        await File.WriteAllBytesAsync(fixture.ConfigPath, original, TestContext.Current.CancellationToken);

        using var result = await fixture.RunAsync("config-invalid-port", expectedExitCode: 1);

        (await File.ReadAllBytesAsync(fixture.ConfigPath, TestContext.Current.CancellationToken)).ShouldBe(original);
        var output = result.RootElement.GetProperty("output").GetString().ShouldNotBeNull();
        output.ShouldContain("'65536' is not a valid port (expected an integer between 1 and 65535).");
        output.ShouldNotContain("defaultPort = 65536");
        result.RootElement.GetProperty("stages").EnumerateArray().Select(stage => stage.GetString())
            .ShouldBe(["invalid-port-refused"]);
    }

    [Fact]
    public async Task Init_PrivateCachedNewerVersion_ShowsUpgradeNoticeWithoutChangingConfigOrCache()
    {
        using var fixture = new InitWizardProcessFixture();
        var original = Encoding.UTF8.GetBytes(ExistingConfig + "\n");
        await File.WriteAllBytesAsync(fixture.ConfigPath, original, TestContext.Current.CancellationToken);
        var cachePath = Path.Join(fixture.Private, "update-cache.json");
        const string cache = """
            {"latestVersion":"9999.0.0","checkedAt":"2000-01-01T00:00:00+00:00"}
            """;
        await File.WriteAllTextAsync(cachePath, cache, TestContext.Current.CancellationToken);

        using var result = await fixture.RunAsync("decline");

        var output = result.RootElement.GetProperty("output").GetString().ShouldNotBeNull();
        output.ShouldContain("↑ v9999.0.0 available");
        output.ShouldContain("run /upgrade for details");
        output.ShouldContain("Reconfigure?");
        output.ShouldNotContain("Environment configured.");
        (await File.ReadAllBytesAsync(fixture.ConfigPath, TestContext.Current.CancellationToken)).ShouldBe(original);
        (await File.ReadAllTextAsync(cachePath, TestContext.Current.CancellationToken)).ShouldBe(cache);
    }
}
