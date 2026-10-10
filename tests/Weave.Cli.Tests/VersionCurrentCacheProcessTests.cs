using System.Text.Json;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class VersionCurrentCacheProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Version_CurrentOrOlderCache_ReportsCachedVersionWithoutUpgradeAdviceOrWrites(bool older)
    {
        using var cli = new IsolatedCliProcess();
        await cli.AssertPrivateHomeAsync();
        var installed = await cli.RunAsync("version");
        installed.ExitCode.ShouldBe(0, installed.StandardError);
        var versionLine = installed.VisibleOutput.Split('\n')[0].TrimEnd('\r');
        versionLine.ShouldStartWith("weave v");
        var current = versionLine["weave v".Length..];
        current.ShouldNotBeNullOrWhiteSpace();
        var latest = older ? "0.0.0" : current;
        if (older)
            VersionService.IsNewer(current, latest).ShouldBeTrue("The older-cache fixture must be older than the installed CLI.");
        var cachePath = Path.Join(cli.WeaveHome, "update-cache.json");
        Directory.CreateDirectory(cli.WeaveHome);
        var cache = JsonSerializer.Serialize(new UpdateCache
        {
            LatestVersion = latest,
            CheckedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        }, VersionJsonContext.Default.UpdateCache);
        await File.WriteAllTextAsync(cachePath, cache, TestContext.Current.CancellationToken);
        var entries = Directory.GetFileSystemEntries(cli.Root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ToArray();

        var result = await cli.RunAsync("version");

        result.ExitCode.ShouldBe(0, result.StandardError);
        result.VisibleOutput.ShouldStartWith(versionLine);
        result.VisibleOutput.ShouldContain("latest on NuGet: v" + latest + " (checked ");
        result.VisibleOutput.ShouldNotContain("is available");
        result.VisibleOutput.ShouldNotContain("Upgrade:");
        result.VisibleOutput.ShouldNotContain("dotnet tool update --global Weave.Cli");
        result.VisibleOutput.ShouldNotContain("no update cache yet");
        (await File.ReadAllTextAsync(cachePath, TestContext.Current.CancellationToken)).ShouldBe(cache);
        File.Exists(cli.ConfigPath).ShouldBeFalse();
        Directory.GetFileSystemEntries(cli.Root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).ShouldBe(entries);
    }
}
