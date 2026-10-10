using Microsoft.Extensions.Time.Testing;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class UpgradeDisabledProcessTests
{
    [Fact]
    public Task ExecuteAsync_UpdateChecksDisabled_ReportsReasonWithoutUsingOrReplacingCache() =>
        SiloLauncherProcessHarness.RunAsync(typeof(UpgradeDisabledProcessTests), async root =>
        {
            Environment.GetEnvironmentVariable("WEAVE_NO_UPDATE_CHECK").ShouldBe("1");
            var cacheDirectory = Path.Join(root, ".weave");
            var cachePath = Path.Join(cacheDirectory, "update-cache.json");
            const string cachedUpdate = """
                {"latestVersion":"9999.0.0","checkedAt":"2026-01-02T03:04:05+00:00"}
                """;
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
            var command = new UpgradeCliCommand(new VersionService(time));

            bool[] cacheStates = [false, true];
            foreach (var hasCache in cacheStates)
            {
                if (hasCache)
                {
                    Directory.CreateDirectory(cacheDirectory);
                    await File.WriteAllTextAsync(cachePath, cachedUpdate, TestContext.Current.CancellationToken);
                    var cached = VersionService.LoadCache().ShouldNotBeNull();
                    cached.LatestVersion.ShouldBe("9999.0.0");
                    cached.CheckedAt.ShouldBe(time.GetUtcNow());
                }
                using var output = new ShellOutputCapture();

                var result = await command.ExecuteAsync(new NoCliOptions(), TestContext.Current.CancellationToken);

                result.ShouldBe(1);
                output.Text.ShouldContain("Installed");
                output.Text.ShouldContain("v" + VersionService.Current());
                output.Text.ShouldContain("Update checks are disabled (WEAVE_NO_UPDATE_CHECK).");
                output.Text.ShouldNotContain("9999.0.0");
                output.Text.ShouldNotContain("Latest");
                output.Text.ShouldNotContain("You are on the latest version.");
                output.Text.ShouldNotContain(VersionService.UpgradeCommand);
                if (hasCache)
                    (await File.ReadAllTextAsync(cachePath, TestContext.Current.CancellationToken)).ShouldBe(cachedUpdate);
                else
                    Directory.Exists(cacheDirectory).ShouldBeFalse();
            }
        });
}
