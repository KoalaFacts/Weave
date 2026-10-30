using Weave.Actions.Config;
using Weave.Actions.SystemInfo;

namespace Weave.Cli.Tests;

[Collection(nameof(ShellConsoleGroup))]
public sealed class ShellConfigurationRendererTests
{
    [Fact]
    public void Render_ConfigSummary_PrintsSettingsAndLiteralMarkupCharacters()
    {
        using var output = new ShellOutputCapture();

        ConfigSummaryRenderer.Render(new ConfigSummary
        {
            Version = "1.0",
            DefaultPort = "4321",
            Storage = "store[fixture]",
            AuthMode = "auth[fixture]",
            RequireHttps = "true",
            SiloPath = "/host[fixture]",
            WeaveHome = "/home[fixture]",
            BaseUrl = "https://fixture.test"
        });

        output.Text.ShouldContain("Configuration");
        output.Text.ShouldContain("4321");
        output.Text.ShouldContain("store[fixture]");
        output.Text.ShouldContain("auth[fixture]");
        output.Text.ShouldContain("/host[fixture]");
        output.Text.ShouldContain("/home[fixture]");
        output.Text.ShouldContain("https://fixture.test");
        output.Text.ShouldContain("requireHttps");
        output.Text.ShouldContain("true");
    }

    [Theory]
    [InlineData(true, "/host[fixture]", true)]
    [InlineData(false, null, false)]
    public void Render_SystemInfo_PrintsReachabilityAndSiloDiscovery(bool reachable, string? siloPath, bool https)
    {
        using var output = new ShellOutputCapture();

        SystemInfoRenderer.Render(new SystemInfoResult
        {
            Reachable = reachable,
            BaseUrl = "http://fixture.test",
            DefaultPort = 4321,
            Storage = "store[fixture]",
            AuthMode = "bearer",
            RequireHttps = https,
            SiloPath = siloPath,
            WeaveHome = "/home[fixture]"
        });

        output.Text.ShouldContain(reachable ? "online · http://fixture.test" : "offline · http://fixture.test");
        output.Text.ShouldNotContain(reachable ? "offline" : "online");
        output.Text.ShouldContain(siloPath ?? "(auto-detect)");
        output.Text.ShouldContain("store[fixture]");
        output.Text.ShouldContain("/home[fixture]");
        output.Text.ShouldContain("4321");
        output.Text.ShouldContain(https ? "true" : "false");
    }
}
