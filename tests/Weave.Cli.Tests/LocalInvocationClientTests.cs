using System.Text.Json.Nodes;
using Shouldly;
using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

public sealed class LocalInvocationClientTests
{
    private const string Id = "82c07b3d2a3646e88f2f0b8db07c452b";

    [Fact]
    public async Task ResumeWrite_ApprovedThenRecorded_UsesEmptyBodyAndNeverDispatchesTwice()
    {
        using var files = new LocalTestDirectory();
        var executed = false;
        using var handler = new LocalHttpFixture((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                executed = true;
                return LocalHttpFixture.Response(200, new JsonObject { ["outcome"] = "Succeeded", ["attemptId"] = "one-attempt" });
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/approval", StringComparison.Ordinal))
                return LocalHttpFixture.Response(200, new JsonObject { ["approvalState"] = "Approved" });
            return executed ? LocalHttpFixture.Response(200, new JsonObject { ["outcome"] = "Succeeded", ["attemptId"] = "one-attempt" })
                : LocalHttpFixture.Response(404);
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var invocations = new LocalInvocationClient(new LocalHttp(client, TimeProvider.System), "onboarding", "test-agent", files.Private);
        var first = await invocations.CallAsync("resume_write", new JsonObject { ["invocation_id"] = Id }, TestContext.Current.CancellationToken);
        first["result"]!["outcome"]!.GetValue<string>().ShouldBe("Succeeded");
        var second = await invocations.CallAsync("resume_write", new JsonObject { ["invocation_id"] = Id }, TestContext.Current.CancellationToken);
        second["invocation"]!["result"]!["attemptId"]!.GetValue<string>().ShouldBe("one-attempt");
        var posts = handler.Requests.Where(request => request.Method == "POST").ToArray();
        posts.Length.ShouldBe(1);
        posts[0].Path.ShouldEndWith("/" + Id + "/resume");
        posts[0].Body.ShouldBeNull();
        handler.Requests.Any(request => request.Operator).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Rejected")]
    [InlineData("Expired")]
    public async Task ResumeWrite_NotApproved_DoesNotPost(string state)
    {
        using var files = new LocalTestDirectory();
        using var handler = new LocalHttpFixture((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/approval", StringComparison.Ordinal)
            ? LocalHttpFixture.Response(200, new JsonObject { ["approvalState"] = state }) : LocalHttpFixture.Response(404));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var invocations = new LocalInvocationClient(new LocalHttp(client, TimeProvider.System), "onboarding", "test-agent", files.Private);
        var result = await invocations.CallAsync("resume_write", new JsonObject { ["invocation_id"] = Id }, TestContext.Current.CancellationToken);
        result["approval"]!["result"]!["approvalState"]!.GetValue<string>().ShouldBe(state);
        handler.Requests.Any(request => request.Method == "POST").ShouldBeFalse();
    }

    [Fact]
    public async Task SubmitWrite_ResponseLost_RetainsReceiptAndDoesNotAutomaticallyRetry()
    {
        using var files = new LocalTestDirectory();
        using var handler = new LocalHttpFixture((request, _) => request.Method == HttpMethod.Post
            ? throw new HttpRequestException("response lost") : LocalHttpFixture.Response(404));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var invocations = new LocalInvocationClient(new LocalHttp(client, TimeProvider.System), "onboarding", "test-agent", files.Private);
        var original = new JsonObject { ["invocation_id"] = Id, ["path"] = "summary.md", ["content"] = "approved only by a human" };
        await Should.ThrowAsync<HttpRequestException>(() => invocations.CallAsync("submit_write", original, TestContext.Current.CancellationToken));
        File.Exists(Path.Combine(files.Private, Id + ".sha256")).ShouldBeTrue();
        var result = await invocations.CallAsync("submit_write", original, TestContext.Current.CancellationToken);
        result["invocation"]!["http_status"]!.GetValue<int>().ShouldBe(404);
        handler.Requests.Count(request => request.Method == "POST").ShouldBe(1);
        original["content"] = "changed";
        await Should.ThrowAsync<ArgumentException>(() => invocations.CallAsync("submit_write", original, TestContext.Current.CancellationToken));
        handler.Requests.Count(request => request.Method == "POST").ShouldBe(1);
    }
}
