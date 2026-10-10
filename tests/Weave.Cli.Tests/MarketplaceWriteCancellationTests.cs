using System.Net;

namespace Weave.Cli.Tests;

[Collection(nameof(ShellConsoleGroup))]
public sealed class MarketplaceWriteCancellationTests
{
    [Theory]
    [InlineData("submit")]
    [InlineData("publish")]
    public async Task UnhealthyServer_DoesNotPromptOrPost(string operation)
    {
        using var console = new MarketplacePromptConsole();
        using var fixture = new MarketplaceWriteFlowFixture();
        fixture.Respond = (_, _) => Task.FromResult(MarketplaceWriteFlowFixture.Response(HttpStatusCode.ServiceUnavailable));

        (await fixture.ExecuteAsync(operation, TestContext.Current.CancellationToken)).ShouldBe(1);

        fixture.Requests.ShouldBe(["GET /health"]);
        fixture.Body.ShouldBeNull();
        console.Text.ShouldContain("Weave server is not running. Start it with 'weave serve'.");
        console.Text.ShouldNotContain("Item name:");
        console.Text.ShouldNotContain("Reviewer ID:");
    }

    [Theory]
    [InlineData("submit")]
    [InlineData("publish")]
    public async Task PendingPost_CallerCancellationReachesHttp_AndDoesNotRenderSuccess(string operation)
    {
        using var console = new MarketplacePromptConsole();
        using var fixture = new MarketplaceWriteFlowFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (operation == "submit")
            MarketplaceWriteFlowFixture.QueueSubmission(console);
        else
            MarketplaceWriteFlowFixture.QueueReview(console, true, "Owned review.");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Respond = async (path, token) =>
        {
            if (path == "/health")
                return MarketplaceWriteFlowFixture.Response(HttpStatusCode.OK);
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
        var pending = fixture.ExecuteAsync(operation, cancellation.Token);
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
        fixture.Requests.ShouldBe(["GET /health", operation == "submit"
            ? "POST /api/marketplace" : "POST /api/marketplace/item%3Fowned/publish"]);
        console.RemainingKeys.ShouldBe(0);
        console.Text.ShouldNotContain("submitted (ID:");
        console.Text.ShouldNotContain("is now published");
        console.Text.ShouldNotContain("Failed to publish:");
    }
}
