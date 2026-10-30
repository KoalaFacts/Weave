using System.Text.Json.Nodes;
using Shouldly;
using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

public sealed class LocalMcpPreflightTests
{
    private const string Id = "82c07b3d2a3646e88f2f0b8db07c452b";

    [Theory]
    [InlineData("submit_write", 401)]
    [InlineData("submit_write", 403)]
    [InlineData("submit_write", 503)]
    [InlineData("submit_write", 404)]
    [InlineData("resume_write", 401)]
    [InlineData("resume_write", 403)]
    [InlineData("resume_write", 503)]
    [InlineData("resume_write", 404)]
    public async Task RunAsync_InvocationPreflightFailure_ReportsToolErrorWithoutLosingEvidence(string name, int status)
    {
        using var files = new LocalTestDirectory();
        var invocation = FailureBody(status);
        var approval = new JsonObject { ["invocationId"] = Id, ["approvalState"] = "Approved" };
        using var handler = new LocalHttpFixture((request, _) => IsApproval(request)
            ? LocalHttpFixture.Response(200, approval)
            : LocalHttpFixture.Response(status, invocation));

        var (isError, response) = await CallAsync(name, handler, files.Private);

        JsonNode.DeepEquals(response, ExpectedStatus(status, invocation, status == 404 ? 200 : null,
            status == 404 ? approval : null, "Unconfirmed")).ShouldBeTrue();
        handler.Requests.Count(request => request.Method == "POST").ShouldBe(0);
        Directory.Exists(files.Private).ShouldBeFalse();
        isError.ShouldBeTrue();
    }

    [Theory]
    [InlineData("submit_write", 401)]
    [InlineData("submit_write", 403)]
    [InlineData("submit_write", 503)]
    [InlineData("submit_write", 404)]
    [InlineData("resume_write", 401)]
    [InlineData("resume_write", 403)]
    [InlineData("resume_write", 503)]
    [InlineData("resume_write", 404)]
    public async Task RunAsync_ApprovalPreflightFailure_ReportsToolErrorWithoutLosingEvidence(string name, int status)
    {
        using var files = new LocalTestDirectory();
        var invocation = new JsonObject { ["errorCode"] = "invocation-not-found", ["marker"] = "original-invocation" };
        var approval = FailureBody(status);
        using var handler = new LocalHttpFixture((request, _) => IsApproval(request)
            ? LocalHttpFixture.Response(status, approval)
            : LocalHttpFixture.Response(404, invocation));

        var (isError, response) = await CallAsync(name, handler, files.Private);

        JsonNode.DeepEquals(response, ExpectedStatus(404, invocation, status, approval, "Unconfirmed")).ShouldBeTrue();
        handler.Requests.Count(request => request.Method == "POST").ShouldBe(0);
        Directory.Exists(files.Private).ShouldBeFalse();
        isError.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false, 401)]
    [InlineData(false, 403)]
    [InlineData(false, 503)]
    [InlineData(false, 404)]
    [InlineData(true, 401)]
    [InlineData(true, 403)]
    [InlineData(true, 503)]
    [InlineData(true, 404)]
    public async Task RunAsync_DiagnosticStatusFailure_PreservesEvidenceWithoutToolError(bool approvalFailure, int status)
    {
        using var files = new LocalTestDirectory();
        var invocationStatus = approvalFailure ? 404 : status;
        var invocation = approvalFailure
            ? new JsonObject { ["errorCode"] = "invocation-not-found" }
            : FailureBody(status);
        var approval = approvalFailure ? FailureBody(status)
            : new JsonObject { ["invocationId"] = Id, ["approvalState"] = "Pending" };
        var approvalStatus = approvalFailure ? status : 200;
        using var handler = new LocalHttpFixture((request, _) => IsApproval(request)
            ? LocalHttpFixture.Response(approvalStatus, approval)
            : LocalHttpFixture.Response(invocationStatus, invocation));

        var (isError, response) = await CallAsync("get_status", handler, files.Private);

        JsonNode.DeepEquals(response, ExpectedStatus(invocationStatus, invocation,
            invocationStatus == 404 ? approvalStatus : null, invocationStatus == 404 ? approval : null,
            "Unconfirmed")).ShouldBeTrue();
        handler.Requests.Count(request => request.Method == "POST").ShouldBe(0);
        isError.ShouldBeFalse();
    }

    [Theory]
    [InlineData("submit_write")]
    [InlineData("resume_write")]
    public async Task RunAsync_MatchingPendingApproval_PreservesExpectedMissingInvocationWithoutToolError(string name)
    {
        using var files = new LocalTestDirectory();
        var invocation = new JsonObject { ["errorCode"] = "invocation-not-found", ["marker"] = "original-invocation" };
        var approval = new JsonObject { ["invocationId"] = Id, ["approvalState"] = "Pending", ["marker"] = "original-approval" };
        using var handler = new LocalHttpFixture((request, _) => IsApproval(request)
            ? LocalHttpFixture.Response(200, approval)
            : LocalHttpFixture.Response(404, invocation));

        var (isError, response) = await CallAsync(name, handler, files.Private);

        JsonNode.DeepEquals(response, ExpectedStatus(404, invocation, 200, approval, "NotStarted")).ShouldBeTrue();
        handler.Requests.Count(request => request.Method == "POST").ShouldBe(0);
        isError.ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_NewWritePending_PreservesAcceptedResponseWithoutToolError()
    {
        using var files = new LocalTestDirectory();
        var pending = new JsonObject { ["invocationId"] = Id, ["approvalState"] = "Pending", ["marker"] = "original-pending" };
        using var handler = new LocalHttpFixture((request, _) => request.Method == HttpMethod.Post
            ? LocalHttpFixture.Response(202, pending)
            : LocalHttpFixture.Response(404, new JsonObject
            {
                ["errorCode"] = IsApproval(request) ? "approval-not-found" : "invocation-not-found"
            }));

        var (isError, response) = await CallAsync("submit_write", handler, files.Private);

        JsonNode.DeepEquals(response, new JsonObject
        {
            ["http_status"] = 202,
            ["result"] = pending.DeepClone(),
            ["invocation_id"] = Id
        }).ShouldBeTrue();
        handler.Requests.Count(request => request.Method == "POST").ShouldBe(1);
        isError.ShouldBeFalse();
    }

    [Theory]
    [InlineData("submit_write", "Succeeded", true)]
    [InlineData("submit_write", "Failed", true)]
    [InlineData("submit_write", "Cancelled", true)]
    [InlineData("submit_write", "Denied", true)]
    [InlineData("submit_write", "NotDispatched", true)]
    [InlineData("submit_write", "OutcomeUnknown", true)]
    [InlineData("submit_write", "OutcomeUnknown", false)]
    [InlineData("resume_write", "Succeeded", true)]
    [InlineData("resume_write", "Failed", true)]
    [InlineData("resume_write", "Cancelled", true)]
    [InlineData("resume_write", "Denied", true)]
    [InlineData("resume_write", "NotDispatched", true)]
    [InlineData("resume_write", "OutcomeUnknown", true)]
    [InlineData("resume_write", "OutcomeUnknown", false)]
    public async Task RunAsync_ExistingAttempt_PreservesOutcomeWithoutToolErrorOrReplay(string name, string outcome, bool recorded)
    {
        using var files = new LocalTestDirectory();
        var invocation = new JsonObject
        {
            ["invocationId"] = Id,
            ["toolName"] = "files",
            ["attemptId"] = "3fdd0938f7c0449888d4fabb8e838d50",
            ["outcome"] = outcome,
            ["outcomeRecorded"] = recorded,
            ["success"] = outcome == "Succeeded"
        };
        using var handler = new LocalHttpFixture((_, _) => LocalHttpFixture.Response(200, invocation));

        var (isError, response) = await CallAsync(name, handler, files.Private);

        JsonNode.DeepEquals(response, ExpectedStatus(200, invocation, null, null, outcome)).ShouldBeTrue();
        handler.Requests.Count(request => request.Method == "POST").ShouldBe(0);
        isError.ShouldBeFalse();
    }

    private static JsonObject FailureBody(int status) => new()
    {
        ["errorCode"] = status switch
        {
            401 => "unauthenticated",
            403 => "forbidden",
            404 => "route-not-found",
            _ => "service-unavailable"
        },
        ["marker"] = "original-http-failure",
        ["details"] = new JsonObject { ["message"] = "Preserve the complete response." }
    };

    private static JsonObject ExpectedStatus(int invocationStatus, JsonObject invocation, int? approvalStatus,
        JsonObject? approval, string executionState)
    {
        return new JsonObject
        {
            ["invocation"] = new JsonObject { ["http_status"] = invocationStatus, ["result"] = invocation.DeepClone() },
            ["approval"] = approvalStatus is null ? null
                : new JsonObject { ["http_status"] = approvalStatus.Value, ["result"] = approval?.DeepClone() },
            ["invocation_id"] = Id,
            ["execution_state"] = executionState
        };
    }

    private static bool IsApproval(HttpRequestMessage request)
    {
        request.RequestUri.ShouldNotBeNull();
        return request.RequestUri.AbsolutePath.EndsWith("/approval", StringComparison.Ordinal);
    }

    private static async Task<(bool IsError, JsonObject Response)> CallAsync(string name, LocalHttpFixture handler, string receipts)
    {
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var server = new LocalMcpServer(new LocalInvocationClient(new LocalHttp(client, TimeProvider.System), "onboarding", "agent", receipts));
        var arguments = new JsonObject { ["invocation_id"] = Id };
        if (name == "submit_write")
        {
            arguments["path"] = "proposal.md";
            arguments["content"] = "original proposal";
        }
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 2,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = name, ["arguments"] = arguments }
        };
        using var input = new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}\n"
            + "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n" + request.ToJsonString());
        using var output = new StringWriter();
        (await server.RunAsync(input, output, TestContext.Current.CancellationToken)).ShouldBe(0);
        var replies = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        replies.Length.ShouldBe(2);
        var reply = JsonNode.Parse(replies[1]).ShouldBeOfType<JsonObject>();
        var result = reply["result"].ShouldBeOfType<JsonObject>();
        var errorNode = result["isError"];
        errorNode.ShouldNotBeNull();
        var isError = errorNode.GetValue<bool>();
        var content = result["content"].ShouldBeOfType<JsonArray>();
        content.Count.ShouldBe(1);
        var textNode = content[0].ShouldBeOfType<JsonObject>()["text"];
        textNode.ShouldNotBeNull();
        var text = textNode.GetValue<string>();
        return (isError, JsonNode.Parse(text).ShouldBeOfType<JsonObject>());
    }
}
