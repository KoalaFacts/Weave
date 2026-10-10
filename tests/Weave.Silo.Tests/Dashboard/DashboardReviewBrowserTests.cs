using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weave.Silo.Tests.Dashboard;

public sealed class DashboardReviewBrowserTests
{
    private const string OriginalBody = "review-content-marker <script>window.__reviewInjected=true</script>\ncomplete retained text";

    [Fact]
    public async Task Review_UploadAndVerify_RendersExactContentAndInvalidatesWhenWorkspaceChanges()
    {
        await using var fixture = new DashboardReviewBrowserFixture();
        await fixture.StartAsync();
        var original = OriginalRequest();
        var path = Path.Join(fixture.Root, "retained-request.json");
        await File.WriteAllTextAsync(path, "\uFEFF" + original, new UTF8Encoding(false), TestContext.Current.CancellationToken);
        await fixture.LoadRequestAsync(path);

        await fixture.ClickVerifyAsync();
        await fixture.Browser.WaitForAsync("document.querySelector('.review-panel h2')?.textContent === 'Verified snapshot'", "verified review snapshot");

        var request = fixture.Api.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe("POST");
        request.Path.ShouldBe(DashboardReviewBrowserApi.ReviewPath);
        request.Body.ShouldBe(original);
        request.Capability.ShouldBe("browser-review-capability");
        request.Authorization.ShouldBe("Bearer browser-global-token");
        var text = await fixture.TextAsync();
        text.ShouldContain("original-release-agent");
        text.ShouldContain("browser-review-plan-marker");
        (await fixture.Browser.EvaluateAsync<string>("document.querySelector('.review-panel pre.body').textContent"))
            .ShouldBe(JsonSerializer.Serialize(OriginalBody));
        (await fixture.Browser.EvaluateAsync<int>("document.querySelectorAll('.review-panel script').length")).ShouldBe(0);
        text.ShouldContain("No approval or execution has occurred");
        (await fixture.Browser.EvaluateAsync<bool>("window.__reviewInjected === undefined")).ShouldBeTrue();
        (await fixture.Browser.EvaluateAsync<string>("document.getElementById('review-capability').value")).ShouldBe("");
        (await fixture.Browser.EvaluateAsync<string>("document.getElementById('review-api-token').value")).ShouldBe("");

        await fixture.FillAsync("review-workspace", "different-lab");
        await fixture.Browser.WaitForAsync("document.querySelector('.review-panel') === null", "changed workspace invalidates verified snapshot");

        (await fixture.TextAsync()).ShouldContain("No verified snapshot is displayed.");
        fixture.Api.Requests.ShouldHaveSingleItem();
        fixture.Browser.BlockedRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Review_UpstreamDeniesReview_ShowsSafeDenialWithoutVerifiedContent()
    {
        await using var fixture = new DashboardReviewBrowserFixture(deny: true);
        await fixture.StartAsync();
        var path = Path.Join(fixture.Root, "retained-request.json");
        await File.WriteAllTextAsync(path, OriginalRequest(), TestContext.Current.CancellationToken);
        await fixture.LoadRequestAsync(path);

        await fixture.ClickVerifyAsync();
        await fixture.Browser.WaitForAsync("document.querySelector('.review-page [role=alert]')?.textContent.includes('Review denied.') === true", "denied review message");

        var text = await fixture.TextAsync();
        text.ShouldContain("An independent reviewer needs read, decision and exact-operation approval authority.");
        text.ShouldNotContain("private-upstream-denial-marker");
        (await fixture.Browser.EvaluateAsync<bool>("document.querySelector('.review-panel') === null")).ShouldBeTrue();
        (await fixture.Browser.EvaluateAsync<string>("document.getElementById('review-capability').value")).ShouldBe("");
        (await fixture.Browser.EvaluateAsync<string>("document.getElementById('review-api-token').value")).ShouldBe("");
        fixture.Api.Requests.ShouldHaveSingleItem().Path.ShouldBe(DashboardReviewBrowserApi.ReviewPath);
        fixture.Browser.BlockedRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Review_ClearWhileVerifying_CancelsUpstreamAndDiscardsRetainedInput()
    {
        await using var fixture = new DashboardReviewBrowserFixture(hold: true);
        await fixture.StartAsync();
        var path = Path.Join(fixture.Root, "retained-request.json");
        await File.WriteAllTextAsync(path, OriginalRequest(), TestContext.Current.CancellationToken);
        await fixture.LoadRequestAsync(path);
        await fixture.ClickVerifyAsync();
        await fixture.Api.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await fixture.Browser.WaitForAsync("document.querySelector('.review-page .actions button:first-child').textContent === 'Verifying…'", "pending verification");

        try
        {
            await fixture.ClickClearAsync();
            await fixture.Api.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            fixture.Api.Release.TrySetResult();
        }
        await fixture.Api.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await fixture.Browser.WaitForAsync("document.querySelector('.review-page').innerText.includes('No file loaded. Select the retained original JSON request.')", "cleared upload state");

        (await fixture.Browser.EvaluateAsync<bool>("document.querySelector('.review-panel') === null")).ShouldBeTrue();
        (await fixture.Browser.EvaluateAsync<bool>("document.querySelector('.review-page .actions button:first-child').disabled")).ShouldBeTrue();
        (await fixture.Browser.EvaluateAsync<int>("document.getElementById('review-file').files.length")).ShouldBe(0);
        (await fixture.Browser.EvaluateAsync<string>("document.getElementById('review-capability').value")).ShouldBe("");
        (await fixture.Browser.EvaluateAsync<string>("document.getElementById('review-api-token').value")).ShouldBe("");
        (await fixture.TextAsync()).ShouldContain("No verified snapshot is displayed.");
        fixture.Api.Requests.ShouldHaveSingleItem().Path.ShouldBe(DashboardReviewBrowserApi.ReviewPath);
        fixture.Browser.BlockedRequests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_InvalidUpload_RejectsBeforeContactingUpstream(bool oversized)
    {
        await using var fixture = new DashboardReviewBrowserFixture();
        await fixture.StartAsync();
        var path = Path.Join(fixture.Root, "invalid-request.json");
        byte[] bytes = oversized ? new byte[1_048_577] : [0xC3, 0x28];
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);

        await fixture.Browser.UploadAsync(path);
        await fixture.Browser.WaitForAsync("document.querySelector('.review-page [role=alert]')?.textContent.includes('Unable to read the file.') === true", "rejected invalid upload");

        (await fixture.TextAsync()).ShouldContain("Select a UTF-8 invocation JSON file no larger than 1 MiB.");
        (await fixture.Browser.EvaluateAsync<bool>("document.querySelector('.review-page .actions button:first-child').disabled")).ShouldBeTrue();
        (await fixture.Browser.EvaluateAsync<bool>("document.querySelector('.review-panel') === null")).ShouldBeTrue();
        fixture.Api.Requests.ShouldBeEmpty();
        fixture.Browser.BlockedRequests.ShouldBeEmpty();
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
