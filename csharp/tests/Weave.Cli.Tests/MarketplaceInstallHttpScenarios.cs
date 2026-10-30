using System.Net;
using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

internal static class MarketplaceInstallHttpScenarios
{
    public static Task RunAsync(Type testClass, Func<Task> scenario) =>
        SiloLauncherProcessHarness.RunAsync(testClass, async root =>
        {
            var before = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal).ToArray();
            await scenario();
            Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal).ShouldBe(before);
        });

    public static async Task InstallRejected_ReturnsFailureWithoutScaffolding(HttpStatusCode status, string expected)
    {
        using var output = new ShellOutputCapture();
        using var fixture = new MarketplaceInstallFlowFixture(new MarketplaceInstallFlowFixture.UnusedRegistry());
        fixture.Respond = (request, _) => Task.FromResult(MarketplaceInstallFlowFixture.Response(
            request.RequestUri.ShouldNotBeNull().AbsolutePath == "/health" ? HttpStatusCode.OK : status,
            "incompatible-template-marker"));

        var result = await fixture.Command.ExecuteAsync(
            new MarketplaceInstallOptions("item?owned", WorkspaceName: "must-not-be-created"),
            TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        fixture.AssertInstallRequests();
        output.Text.ShouldContain(expected);
        output.Text.ShouldNotContain("Installed:");
        output.Text.ShouldNotContain("scaffolded");
    }

    public static async Task ServerUnavailable_StopsAtHealthProbe()
    {
        using var output = new ShellOutputCapture();
        using var fixture = new MarketplaceInstallFlowFixture(new MarketplaceInstallFlowFixture.UnusedRegistry());
        fixture.Respond = (_, _) => Task.FromResult(MarketplaceInstallFlowFixture.Response(HttpStatusCode.ServiceUnavailable));

        var result = await fixture.Command.ExecuteAsync(
            new MarketplaceInstallOptions("item?owned", WorkspaceName: "must-not-be-created"), TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        fixture.Requests.ShouldBe(["GET /health"]);
        output.Text.ShouldContain("Weave server is not running. Start it with 'weave serve'.");
        output.Text.ShouldNotContain("Installed:");
    }

    public static async Task NoItemSuppliedAndCatalogEmpty_DoesNotAttemptInstallOrPrompt()
    {
        using var output = new ShellOutputCapture();
        using var fixture = new MarketplaceInstallFlowFixture(new MarketplaceInstallFlowFixture.UnusedRegistry());
        fixture.Respond = (_, _) => Task.FromResult(MarketplaceInstallFlowFixture.Response(HttpStatusCode.OK, "[]"));

        var result = await fixture.Command.ExecuteAsync(new MarketplaceInstallOptions(null, WorkspaceName: "must-not-be-created"), TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        fixture.Requests.ShouldBe(["GET /health", "GET /api/marketplace"]);
        output.Text.ShouldContain("No marketplace items found.");
        output.Text.ShouldNotContain("Installed:");
    }

    public static async Task CancellationDuringInstall_ReachesHttpAndReturns130WithoutScaffolding()
    {
        using var output = new ShellOutputCapture();
        using var fixture = new MarketplaceInstallFlowFixture(new MarketplaceInstallFlowFixture.UnusedRegistry());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Respond = async (request, token) =>
        {
            if (request.RequestUri.ShouldNotBeNull().AbsolutePath == "/health")
                return MarketplaceInstallFlowFixture.Response(HttpStatusCode.OK);
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("An infinite delay must not complete normally.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                cancelled.TrySetResult();
                throw;
            }
        };
        var pending = fixture.Command.ExecuteAsync(new MarketplaceInstallOptions("item?owned", WorkspaceName: "must-not-be-created"), cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            await cancellation.CancelAsync();
            await pending.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }

        (await pending).ShouldBe(130);
        cancelled.Task.IsCompletedSuccessfully.ShouldBeTrue();
        fixture.AssertInstallRequests();
        output.Text.ShouldBeEmpty();
    }
}
