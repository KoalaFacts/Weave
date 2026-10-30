using System.Net;
using System.Text.RegularExpressions;
using Weave.Actions.SystemInfo;

namespace Weave.Cli.Tests;

[Collection("Tui view console")]
public sealed class TuiWorkspaceDashboardViewTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK, "online")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "offline")]
    public async Task RefreshAsync_EmptyRegistry_ShowsCreationHintAndHealthState(HttpStatusCode health, string expected)
    {
        using var context = new TuiViewTestContext();
        context.Respond = _ => TuiViewTestContext.Json("{}", health);
        var dashboard = Dashboard(context, new Dictionary<string, string>());

        await dashboard.RefreshAsync(TestContext.Current.CancellationToken);

        context.Output.ShouldContain("No workspaces registered yet. Type /new to see how to create one.");
        context.Output.ShouldContain(expected);
        context.Requests.ShouldBe(["/health"]);
        StatCounts(context.Output).ShouldBe(["0", "0", "0"]);
    }

    [Fact]
    public async Task RefreshAsync_ReadyTrackedAndMissingWorkspaces_ShowsSortedRowsAndAccurateTotals()
    {
        using var context = new TuiViewTestContext();
        context.Respond = _ => TuiViewTestContext.Json("{}");
        var idle = context.WriteManifest("idle", """{"version":"1.0","workspace":{"isolation":"full"},"name":"idle-space"}""");
        var missing = context.WriteManifest("missing", "{}");
        File.Delete(missing);
        TuiViewTestContext.WriteState(context.ManifestPath);
        var dashboard = Dashboard(context, new Dictionary<string, string>
        {
            ["zeta-missing"] = Path.GetDirectoryName(missing).ShouldNotBeNull(),
            ["alpha[tracked]"] = Path.GetDirectoryName(context.ManifestPath).ShouldNotBeNull(),
            ["middle-idle"] = Path.GetDirectoryName(idle).ShouldNotBeNull()
        });

        await dashboard.RefreshAsync(TestContext.Current.CancellationToken);

        StatCounts(context.Output).ShouldBe(["3", "2", "1"]);
        context.Output.ShouldContain("online");
        var trackedRow = WorkspaceRow(context.Output, "alpha[tracked]");
        trackedRow.ShouldContain("Ready");
        trackedRow.ShouldContain("Tracked");
        trackedRow.ShouldContain("full");
        Regex.Matches(trackedRow, @"\b\d+\b").Select(match => match.Value).ShouldBe(["1", "1"]);
        var idleRow = WorkspaceRow(context.Output, "middle-idle");
        idleRow.ShouldContain("Ready");
        idleRow.ShouldContain("Idle");
        var missingRow = WorkspaceRow(context.Output, "zeta-missing");
        missingRow.ShouldContain("Missing");
        missingRow.ShouldContain("Idle");
        context.Output.IndexOf("alpha[tracked]", StringComparison.Ordinal)
            .ShouldBeLessThan(context.Output.IndexOf("middle-idle", StringComparison.Ordinal));
        context.Output.IndexOf("middle-idle", StringComparison.Ordinal)
            .ShouldBeLessThan(context.Output.IndexOf("zeta-missing", StringComparison.Ordinal));
        context.Requests.ShouldBe(["/health"]);
    }

    [Fact]
    public async Task RefreshAsync_InvalidManifest_ReportsParseFailureAndDoesNotShowReadyRow()
    {
        using var context = new TuiViewTestContext();
        context.Respond = _ => throw new HttpRequestException("offline-dashboard");
        File.WriteAllText(context.ManifestPath, "{invalid-json");
        var dashboard = Dashboard(context, new Dictionary<string, string>
        {
            ["broken-workspace"] = Path.GetDirectoryName(context.ManifestPath).ShouldNotBeNull()
        });

        await dashboard.RefreshAsync(TestContext.Current.CancellationToken);

        context.Output.ShouldContain("skipped invalid manifest at");
        context.Output.ShouldContain(context.ManifestPath);
        context.Output.ShouldContain("offline");
        var row = WorkspaceRow(context.Output, "broken-workspace");
        row.ShouldNotContain("Ready");
    }

    private static TuiWorkspaceDashboard Dashboard(TuiViewTestContext context, IReadOnlyDictionary<string, string> entries) =>
        new(new GetSystemInfoAction(new TuiViewTestContext.ConfigSource(), context.Client),
            new TuiViewTestContext.ReadOnlyRegistry(entries));

    private static string WorkspaceRow(string output, string name) =>
        output.Split('\n').Single(line => line.Contains(name, StringComparison.Ordinal));

    private static IEnumerable<string> StatCounts(string output)
    {
        var lines = output.Split('\n');
        var heading = Array.FindIndex(lines, line => line.Contains("Tracked runs", StringComparison.Ordinal));
        heading.ShouldBeGreaterThanOrEqualTo(0);
        return Regex.Matches(lines[heading + 1], @"\b\d+\b").Select(match => match.Value);
    }
}
