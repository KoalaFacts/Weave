using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Weave.Silo.Tests.Dashboard;

[Trait("Category", "Integration")]
public sealed class DashboardReviewBrowserTests
{
    private const string OriginalBody = "review-content-marker <script>window.__reviewInjected=true</script>\ncomplete retained text";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Layout_HomeApiUnavailable_ShowsErrorAndRetainsNavigation(bool mobile)
    {
        await using var fixture = new DashboardReviewBrowserFixture(mobile: mobile);
        await fixture.StartAsync();

        await fixture.Page.GetByRole(AriaRole.Link, new() { Name = "Home", Exact = true }).ClickAsync();
        await Expect(fixture.Page.Locator("main")).ToContainTextAsync("Welcome to Weave");
        await Expect(fixture.Page.Locator("main")).ToContainTextAsync("Unable to connect to the Weave Silo API.");
        await fixture.Evidence.CaptureAsync("home-api-unavailable", reviewPage: false);

        await fixture.Page.GetByRole(AriaRole.Link, new() { Name = "Approval review", Exact = true }).ClickAsync();
        await Expect(fixture.Page.Locator(".review-page")).ToContainTextAsync("No verified snapshot is displayed.");
        fixture.Api.Requests.ShouldNotBeEmpty();
        foreach (var request in fixture.Api.Requests)
        {
            request.Method.ShouldBe("GET");
            request.Path.ShouldBe("/api/workspaces");
            request.Capability.ShouldBe("");
            request.Authorization.ShouldBe("");
        }
        fixture.BlockedRequests.ShouldBeEmpty();
        fixture.Evidence.AssertLayouts();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_UploadAndVerify_RendersExactContentAndInvalidatesWhenWorkspaceChanges(bool mobile)
    {
        await using var fixture = new DashboardReviewBrowserFixture(mobile: mobile);
        await fixture.StartAsync();
        await fixture.Evidence.CaptureAsync("empty");
        var original = OriginalRequest();
        var path = Path.Join(fixture.Root, "retained-request.json");
        await File.WriteAllTextAsync(path, "\uFEFF" + original, new UTF8Encoding(false), TestContext.Current.CancellationToken);
        await fixture.LoadRequestAsync(path);
        await fixture.Evidence.CaptureAsync("loaded");

        await fixture.ClickVerifyAsync();
        await Expect(fixture.Page.Locator(".review-panel h2")).ToHaveTextAsync("Verified snapshot");

        await fixture.Evidence.CaptureAsync("verified");
        (await fixture.TextAsync()).ShouldNotContain("Not yet verified");
        var request = fixture.Api.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe("POST");
        request.Path.ShouldBe(DashboardReviewBrowserApi.ReviewPath);
        request.Body.ShouldBe(original);
        request.Capability.ShouldBe("browser-review-capability");
        request.Authorization.ShouldBe("Bearer browser-global-token");
        var text = await fixture.TextAsync();
        text.ShouldContain("original-release-agent");
        text.ShouldContain("browser-review-plan-marker");
        (await fixture.Page.Locator(".review-panel pre.body").TextContentAsync())
            .ShouldBe(JsonSerializer.Serialize(OriginalBody));
        (await fixture.Page.Locator(".review-panel script").CountAsync()).ShouldBe(0);
        text.ShouldContain("No approval or execution has occurred");
        (await fixture.Page.EvaluateAsync<bool>("window.__reviewInjected === undefined")).ShouldBeTrue();
        (await fixture.Page.Locator("#review-capability").InputValueAsync()).ShouldBe("");
        (await fixture.Page.Locator("#review-api-token").InputValueAsync()).ShouldBe("");

        await fixture.FillAsync("review-workspace", "different-lab");
        await Expect(fixture.Page.Locator(".review-panel")).ToHaveCountAsync(0);

        await fixture.Evidence.CaptureAsync("workspace-changed");
        (await fixture.TextAsync()).ShouldContain("No verified snapshot is displayed.");
        fixture.Api.Requests.ShouldHaveSingleItem();
        fixture.BlockedRequests.ShouldBeEmpty();
        fixture.Evidence.AssertLayouts();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_UpstreamDeniesReview_ShowsSafeDenialWithoutVerifiedContent(bool mobile)
    {
        await using var fixture = new DashboardReviewBrowserFixture(deny: true, mobile: mobile);
        await fixture.StartAsync();
        var path = Path.Join(fixture.Root, "retained-request.json");
        await File.WriteAllTextAsync(path, OriginalRequest(), TestContext.Current.CancellationToken);
        await fixture.LoadRequestAsync(path);

        await fixture.ClickVerifyAsync();
        await Expect(fixture.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Review denied.");

        await fixture.Evidence.CaptureAsync("denied");
        var text = await fixture.TextAsync();
        text.ShouldContain("An independent reviewer needs read, decision and exact-operation approval authority.");
        text.ShouldNotContain("private-upstream-denial-marker");
        (await fixture.Page.Locator(".review-panel").CountAsync()).ShouldBe(0);
        (await fixture.Page.Locator("#review-capability").InputValueAsync()).ShouldBe("");
        (await fixture.Page.Locator("#review-api-token").InputValueAsync()).ShouldBe("");
        fixture.Api.Requests.ShouldHaveSingleItem().Path.ShouldBe(DashboardReviewBrowserApi.ReviewPath);
        fixture.BlockedRequests.ShouldBeEmpty();
        fixture.Evidence.AssertLayouts();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_ClearWhileVerifying_CancelsUpstreamAndDiscardsRetainedInput(bool mobile)
    {
        await using var fixture = new DashboardReviewBrowserFixture(hold: true, mobile: mobile);
        await fixture.StartAsync();
        var path = Path.Join(fixture.Root, "retained-request.json");
        await File.WriteAllTextAsync(path, OriginalRequest(), TestContext.Current.CancellationToken);
        await fixture.LoadRequestAsync(path);
        await fixture.ClickVerifyAsync();
        await fixture.Api.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Expect(fixture.Page.Locator(".review-page .actions button").First).ToHaveTextAsync("Verifying…");

        try
        {
            await fixture.Evidence.CaptureAsync("verifying");
            await fixture.ClickClearAsync();
            await fixture.Api.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            fixture.Api.Release.TrySetResult();
        }
        await fixture.Api.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Expect(fixture.Page.Locator(".review-page")).ToContainTextAsync("No file loaded. Select the retained original JSON request.");

        await fixture.Evidence.CaptureAsync("cancelled-and-cleared");
        (await fixture.Page.Locator(".review-panel").CountAsync()).ShouldBe(0);
        (await fixture.Page.Locator(".review-page .actions button").First.IsDisabledAsync()).ShouldBeTrue();
        (await fixture.Page.EvaluateAsync<int>("document.getElementById('review-file').files.length")).ShouldBe(0);
        (await fixture.Page.Locator("#review-capability").InputValueAsync()).ShouldBe("");
        (await fixture.Page.Locator("#review-api-token").InputValueAsync()).ShouldBe("");
        (await fixture.TextAsync()).ShouldContain("No verified snapshot is displayed.");
        fixture.Api.Requests.ShouldHaveSingleItem().Path.ShouldBe(DashboardReviewBrowserApi.ReviewPath);
        fixture.BlockedRequests.ShouldBeEmpty();
        fixture.Evidence.AssertLayouts();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Review_InvalidUpload_RejectsBeforeContactingUpstream(bool oversized, bool mobile)
    {
        await using var fixture = new DashboardReviewBrowserFixture(mobile: mobile);
        await fixture.StartAsync();
        var path = Path.Join(fixture.Root, "invalid-request.json");
        byte[] bytes = oversized ? new byte[1_048_577] : [0xC3, 0x28];
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);

        await fixture.Page.Locator("#review-file").SetInputFilesAsync(path);
        await Expect(fixture.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Unable to read the file.");

        await fixture.Evidence.CaptureAsync(oversized ? "oversized-rejected" : "invalid-utf8-rejected");
        (await fixture.TextAsync()).ShouldContain("Select a UTF-8 invocation JSON file no larger than 1 MiB.");
        (await fixture.Page.Locator(".review-page .actions button").First.IsDisabledAsync()).ShouldBeTrue();
        (await fixture.Page.Locator(".review-panel").CountAsync()).ShouldBe(0);
        fixture.Api.Requests.ShouldBeEmpty();
        fixture.BlockedRequests.ShouldBeEmpty();
        fixture.Evidence.AssertLayouts();
    }

    [Fact]
    public async Task Evidence_MobileSmoothScrolling_CapturesFromTopAndRestoresOriginalPosition()
    {
        await using var fixture = new DashboardReviewBrowserFixture(mobile: true);
        await fixture.StartAsync();
        var previous = await fixture.Page.EvaluateAsync<int>("""
            () => {
              document.documentElement.style.scrollBehavior = 'smooth';
              document.body.style.minHeight = '2400px';
              window.scrollTo({left: 0, top: 482, behavior: 'instant'});
              return window.scrollY;
            }
            """);
        previous.ShouldBeGreaterThan(0, "A scrolled, smooth-scrolling page must exercise the tiled capture regression.");

        await fixture.Evidence.CaptureAsync("smooth-scroll-regression");

        (await fixture.Page.EvaluateAsync<int>("window.scrollY")).ShouldBe(previous,
            "Capturing all mobile tiles must restore the caller's original scroll position.");
        fixture.BlockedRequests.ShouldBeEmpty();
    }

    private static string OriginalRequest() => new JsonObject
    {
        ["invocationId"] = "abcdef0123456789abcdef0123456789",
        ["toolName"] = "files",
        ["method"] = "write_file",
        ["parameters"] = new JsonObject { ["path"] = "release-note.txt" },
        ["rawInput"] = OriginalBody
    }.ToJsonString();
}
