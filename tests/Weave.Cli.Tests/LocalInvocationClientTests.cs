using System.Text.Json.Nodes;
using Shouldly;
using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

public sealed class LocalInvocationClientTests
{
    private const string Id = "82c07b3d2a3646e88f2f0b8db07c452b";

    [Theory]
    [InlineData("invocation-not-found", Id, "Approved", "NotStarted", 1)]
    [InlineData("invocation-not-found", Id, "Pending", "NotStarted", 0)]
    [InlineData("invocation-not-found", Id, "Rejected", "NotStarted", 0)]
    [InlineData("invocation-not-found", Id, "Expired", "NotStarted", 0)]
    [InlineData("invocation-not-found", Id, "Cancelled", "NotStarted", 0)]
    [InlineData("invocation-not-found", Id, "Consumed", "Unconfirmed", 0)]
    [InlineData("invocation-not-found", Id, "unrecognized", "Unconfirmed", 0)]
    [InlineData("invocation-not-found", "different-id", "Approved", "Unconfirmed", 0)]
    [InlineData("route-not-found", Id, "Approved", "Unconfirmed", 0)]
    [InlineData(null, Id, "Approved", "Unconfirmed", 0)]
    public async Task StatusAsync_MissingExecution_RequiresMatchingRetainedApprovalBeforeResume(
        string? errorCode, string approvalId, string approvalState, string executionState, int expectedPosts)
    {
        using var files = new LocalTestDirectory();
        using var handler = new LocalHttpFixture((request, _) => request.Method == HttpMethod.Post
            ? LocalHttpFixture.Response(200)
            : request.RequestUri!.AbsolutePath.EndsWith("/approval", StringComparison.Ordinal)
                ? LocalHttpFixture.Response(200, new JsonObject { ["invocationId"] = approvalId, ["approvalState"] = approvalState })
                : LocalHttpFixture.Response(404, new JsonObject { ["errorCode"] = errorCode }));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var invocations = new LocalInvocationClient(new LocalHttp(client, TimeProvider.System), "onboarding", "test-agent", files.Private);
        var status = await invocations.StatusAsync(Id, TestContext.Current.CancellationToken);
        (status["execution_state"]?.GetValue<string>()).ShouldBe(executionState);
        status["invocation"]!["http_status"]!.GetValue<int>().ShouldBe(404);
        await invocations.CallAsync("resume_write", new JsonObject { ["invocation_id"] = Id }, TestContext.Current.CancellationToken);
        handler.Requests.Count(request => request.Method == "POST").ShouldBe(expectedPosts);
    }

    [Theory]
    [InlineData("OutcomeUnknown", false, "OutcomeUnknown")]
    [InlineData("OutcomeUnknown", true, "OutcomeUnknown")]
    [InlineData("Succeeded", true, "Succeeded")]
    [InlineData("Failed", true, "Failed")]
    [InlineData("Cancelled", true, "Cancelled")]
    [InlineData("Denied", true, "Denied")]
    [InlineData("NotDispatched", true, "NotDispatched")]
    [InlineData("Succeeded", false, "Unconfirmed")]
    [InlineData("unrecognized", true, "Unconfirmed")]
    public async Task StatusAsync_ExistingAttempt_ReportsOutcomeAndNeverResumes(string outcome, bool recorded, string executionState)
    {
        using var files = new LocalTestDirectory();
        using var handler = new LocalHttpFixture((_, _) => LocalHttpFixture.Response(200, new JsonObject
        {
            ["invocationId"] = Id,
            ["toolName"] = "files",
            ["attemptId"] = "3fdd0938f7c0449888d4fabb8e838d50",
            ["success"] = outcome == "Succeeded",
            ["outcome"] = outcome,
            ["outcomeRecorded"] = recorded
        }));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var invocations = new LocalInvocationClient(new LocalHttp(client, TimeProvider.System), "onboarding", "test-agent", files.Private);
        var status = await invocations.CallAsync("resume_write", new JsonObject { ["invocation_id"] = Id }, TestContext.Current.CancellationToken);
        (status["execution_state"]?.GetValue<string>()).ShouldBe(executionState);
        handler.Requests.Any(request => request.Method == "POST" || request.Path.EndsWith("/approval", StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(403, 200)]
    [InlineData(503, 200)]
    [InlineData(404, 403)]
    [InlineData(404, 404)]
    [InlineData(404, 503)]
    public async Task ResumeWrite_UnconfirmedResponses_DoesNotDispatch(int invocationStatus, int approvalStatus)
    {
        using var files = new LocalTestDirectory();
        using var handler = new LocalHttpFixture((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/approval", StringComparison.Ordinal)
            ? LocalHttpFixture.Response(approvalStatus, new JsonObject { ["invocationId"] = Id, ["approvalState"] = "Approved" })
            : LocalHttpFixture.Response(invocationStatus, new JsonObject { ["errorCode"] = "invocation-not-found" }));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var invocations = new LocalInvocationClient(new LocalHttp(client, TimeProvider.System), "onboarding", "test-agent", files.Private);
        var status = await invocations.CallAsync("resume_write", new JsonObject { ["invocation_id"] = Id }, TestContext.Current.CancellationToken);
        (status["execution_state"]?.GetValue<string>()).ShouldBe("Unconfirmed");
        handler.Requests.Any(request => request.Method == "POST").ShouldBeFalse();
    }

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
                return LocalHttpFixture.Response(200, new JsonObject { ["invocationId"] = Id, ["toolName"] = "files", ["success"] = true, ["outcomeRecorded"] = true, ["outcome"] = "Succeeded", ["attemptId"] = "3fdd0938f7c0449888d4fabb8e838d50" });
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/approval", StringComparison.Ordinal))
                return LocalHttpFixture.Response(200, new JsonObject { ["invocationId"] = Id, ["approvalState"] = "Approved" });
            return executed ? LocalHttpFixture.Response(200, new JsonObject { ["invocationId"] = Id, ["toolName"] = "files", ["success"] = true, ["outcomeRecorded"] = true, ["outcome"] = "Succeeded", ["attemptId"] = "3fdd0938f7c0449888d4fabb8e838d50" })
                : LocalHttpFixture.Response(404, new JsonObject { ["errorCode"] = "invocation-not-found" });
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var invocations = new LocalInvocationClient(new LocalHttp(client, TimeProvider.System), "onboarding", "test-agent", files.Private);
        var first = await invocations.CallAsync("resume_write", new JsonObject { ["invocation_id"] = Id }, TestContext.Current.CancellationToken);
        first["result"]!["outcome"]!.GetValue<string>().ShouldBe("Succeeded");
        var second = await invocations.CallAsync("resume_write", new JsonObject { ["invocation_id"] = Id }, TestContext.Current.CancellationToken);
        second["invocation"]!["result"]!["attemptId"]!.GetValue<string>().ShouldBe("3fdd0938f7c0449888d4fabb8e838d50");
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
            ? LocalHttpFixture.Response(200, new JsonObject { ["invocationId"] = Id, ["approvalState"] = state }) : LocalHttpFixture.Response(404, new JsonObject { ["errorCode"] = "invocation-not-found" }));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var invocations = new LocalInvocationClient(new LocalHttp(client, TimeProvider.System), "onboarding", "test-agent", files.Private);
        var result = await invocations.CallAsync("resume_write", new JsonObject { ["invocation_id"] = Id }, TestContext.Current.CancellationToken);
        result["approval"]!["result"]!["approvalState"]!.GetValue<string>().ShouldBe(state);
        handler.Requests.Any(request => request.Method == "POST").ShouldBeFalse();
    }

    [Theory]
    [InlineData(403, "forbidden", true)]
    [InlineData(503, "journal-write-failed", true)]
    [InlineData(503, "service-unavailable", false)]
    [InlineData(403, "unknown-denial", false)]
    public async Task SubmitWrite_DefinitivePreAdmissionDenial_AllowsOnlyExplicitSameBodyRetry(int status, string error, bool retryable)
    {
        using var files = new LocalTestDirectory();
        var posts = 0;
        using var handler = new LocalHttpFixture((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posts++;
                return posts == 1 ? LocalHttpFixture.Response(status, new JsonObject { ["errorCode"] = error })
                    : LocalHttpFixture.Response(202, new JsonObject { ["approvalState"] = "Pending" });
            }
            return LocalHttpFixture.Response(404, new JsonObject
            {
                ["errorCode"] = request.RequestUri!.AbsolutePath.EndsWith("/approval", StringComparison.Ordinal)
                ? "approval-not-found" : "invocation-not-found"
            });
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var invocation = new LocalInvocationClient(new LocalHttp(client, TimeProvider.System), "onboarding", "agent", files.Private);
        var original = new JsonObject { ["invocation_id"] = Id, ["path"] = "summary.md", ["content"] = "original frozen body" };
        (await invocation.CallAsync("submit_write", original, TestContext.Current.CancellationToken))["http_status"]!.GetValue<int>().ShouldBe(status);
        posts.ShouldBe(1);
        var changed = (JsonObject)original.DeepClone();
        changed["content"] = "replacement";
        await Should.ThrowAsync<ArgumentException>(() => invocation.CallAsync("submit_write", changed, TestContext.Current.CancellationToken));
        await invocation.CallAsync("submit_write", original, TestContext.Current.CancellationToken);
        posts.ShouldBe(retryable ? 2 : 1);
        handler.Requests.Where(request => request.Method == "POST").All(request => request.Body!["invocationId"]!.GetValue<string>() == Id
            && request.Body["rawInput"]!.GetValue<string>() == "original frozen body").ShouldBeTrue();
        await invocation.CallAsync("submit_write", original, TestContext.Current.CancellationToken);
        posts.ShouldBe(retryable ? 2 : 1);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(503)]
    [InlineData(200)]
    public async Task SubmitWrite_RejectionThenQueryUnconfirmedOrRecorded_RemainsQueryOnly(int lookupStatus)
    {
        using var files = new LocalTestDirectory();
        var posted = false;
        using var handler = new LocalHttpFixture((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted = true;
                return LocalHttpFixture.Response(403, new JsonObject { ["errorCode"] = "forbidden" });
            }
            if (posted)
                return LocalHttpFixture.Response(lookupStatus, new JsonObject { ["outcome"] = "Denied" });
            return LocalHttpFixture.Response(404, new JsonObject
            {
                ["errorCode"] = request.RequestUri!.AbsolutePath.EndsWith("/approval", StringComparison.Ordinal)
                ? "approval-not-found" : "invocation-not-found"
            });
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var invocation = new LocalInvocationClient(new LocalHttp(client, TimeProvider.System), "onboarding", "agent", files.Private);
        var original = new JsonObject { ["invocation_id"] = Id, ["path"] = "summary.md", ["content"] = "original" };
        await invocation.CallAsync("submit_write", original, TestContext.Current.CancellationToken);
        await invocation.CallAsync("submit_write", original, TestContext.Current.CancellationToken);
        handler.Requests.Count(request => request.Method == "POST").ShouldBe(1);
        File.ReadAllText(Path.Join(files.Private, Id + ".sha256")).ShouldNotStartWith("retryable:");
    }

    [Theory]
    [InlineData("route-not-found", "approval-not-found")]
    [InlineData("invocation-not-found", "route-not-found")]
    public async Task SubmitWrite_GenericMissingRoute_NeverSubmits(string invocationError, string approvalError)
    {
        using var files = new LocalTestDirectory();
        using var handler = new LocalHttpFixture((request, _) => LocalHttpFixture.Response(404, new JsonObject
        { ["errorCode"] = request.RequestUri!.AbsolutePath.EndsWith("/approval", StringComparison.Ordinal) ? approvalError : invocationError }));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var invocation = new LocalInvocationClient(new LocalHttp(client, TimeProvider.System), "onboarding", "agent", files.Private);
        await invocation.CallAsync("submit_write", new JsonObject { ["invocation_id"] = Id, ["path"] = "summary.md", ["content"] = "original" }, TestContext.Current.CancellationToken);
        handler.Requests.Any(request => request.Method == "POST").ShouldBeFalse();
        Directory.Exists(files.Private).ShouldBeFalse();
    }

    [Fact]
    public async Task SubmitWrite_CancelledAfterPost_PreservesUncertainReceiptWithoutRetry()
    {
        using var files = new LocalTestDirectory();
        using var cancellation = new CancellationTokenSource();
        using var handler = new LocalHttpFixture((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            return LocalHttpFixture.Response(404, new JsonObject
            {
                ["errorCode"] = request.RequestUri!.AbsolutePath.EndsWith("/approval", StringComparison.Ordinal)
                ? "approval-not-found" : "invocation-not-found"
            });
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var invocation = new LocalInvocationClient(new LocalHttp(client, TimeProvider.System), "onboarding", "agent", files.Private);
        var original = new JsonObject { ["invocation_id"] = Id, ["path"] = "summary.md", ["content"] = "original" };
        await Should.ThrowAsync<OperationCanceledException>(() => invocation.CallAsync("submit_write", original, cancellation.Token));
        File.ReadAllText(Path.Join(files.Private, Id + ".sha256")).ShouldNotStartWith("retryable:");
        await invocation.CallAsync("submit_write", original, TestContext.Current.CancellationToken);
        handler.Requests.Count(request => request.Method == "POST").ShouldBe(1);
    }

    [Fact]
    public async Task SubmitWrite_ResponseLost_RetainsReceiptAndDoesNotAutomaticallyRetry()
    {
        using var files = new LocalTestDirectory();
        using var handler = new LocalHttpFixture((request, _) => request.Method == HttpMethod.Post
            ? throw new HttpRequestException("response lost") : LocalHttpFixture.Response(404, new JsonObject
            { ["errorCode"] = request.RequestUri!.AbsolutePath.EndsWith("/approval", StringComparison.Ordinal) ? "approval-not-found" : "invocation-not-found" }));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var invocations = new LocalInvocationClient(new LocalHttp(client, TimeProvider.System), "onboarding", "test-agent", files.Private);
        var original = new JsonObject { ["invocation_id"] = Id, ["path"] = "summary.md", ["content"] = "approved only by a human" };
        await Should.ThrowAsync<HttpRequestException>(() => invocations.CallAsync("submit_write", original, TestContext.Current.CancellationToken));
        File.Exists(Path.Join(files.Private, Id + ".sha256")).ShouldBeTrue();
        var result = await invocations.CallAsync("submit_write", original, TestContext.Current.CancellationToken);
        result["invocation"]!["http_status"]!.GetValue<int>().ShouldBe(404);
        handler.Requests.Count(request => request.Method == "POST").ShouldBe(1);
        original["content"] = "changed";
        await Should.ThrowAsync<ArgumentException>(() => invocations.CallAsync("submit_write", original, TestContext.Current.CancellationToken));
        handler.Requests.Count(request => request.Method == "POST").ShouldBe(1);
    }
}
