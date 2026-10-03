using System.Text;
using System.Text.Json.Nodes;
using Shouldly;
using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

public sealed class LocalMcpServerTests
{
    private static readonly string[] BusinessTools = ["get_status", "read_document", "resume_write", "submit_write"];

    [Theory]
    [InlineData(404, "NotStarted")]
    [InlineData(200, "OutcomeUnknown")]
    [InlineData(403, "Unconfirmed")]
    public async Task RunAsync_StatusQuery_ExposesExecutionStateAndRawEvidenceToAgent(int httpStatus, string executionState)
    {
        const string id = "82c07b3d2a3646e88f2f0b8db07c452b";
        using var files = new LocalTestDirectory();
        using var handler = new LocalHttpFixture((request, _) => request.RequestUri!.AbsolutePath.EndsWith("/approval", StringComparison.Ordinal)
            ? LocalHttpFixture.Response(200, new JsonObject { ["invocationId"] = id, ["approvalState"] = "Approved" })
            : LocalHttpFixture.Response(httpStatus, httpStatus == 200 ? new JsonObject
            {
                ["invocationId"] = id,
                ["toolName"] = "files",
                ["attemptId"] = "3fdd0938f7c0449888d4fabb8e838d50",
                ["outcome"] = "OutcomeUnknown",
                ["outcomeRecorded"] = false,
                ["success"] = false
            } : new JsonObject { ["errorCode"] = httpStatus == 404 ? "invocation-not-found" : "forbidden" }));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var server = new LocalMcpServer(new LocalInvocationClient(new LocalHttp(client, TimeProvider.System), "onboarding", "agent", files.Private));
        using var input = new StringReader("""
            {"jsonrpc":"2.0","id":1,"method":"initialize"}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_status","arguments":{"invocation_id":"82c07b3d2a3646e88f2f0b8db07c452b"}}}
            """);
        using var output = new StringWriter();
        (await server.RunAsync(input, output, TestContext.Current.CancellationToken)).ShouldBe(0);
        var reply = JsonNode.Parse(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[1])!;
        reply["result"]!["isError"]!.GetValue<bool>().ShouldBeFalse();
        var status = JsonNode.Parse(reply["result"]!["content"]![0]!["text"]!.GetValue<string>())!;
        status["execution_state"]!.GetValue<string>().ShouldBe(executionState);
        status["invocation"]!["http_status"]!.GetValue<int>().ShouldBe(httpStatus);
        status["invocation_id"]!.GetValue<string>().ShouldBe(id);
        handler.Requests.All(request => request.Method == "GET" && !request.Operator).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_InvalidUtf8_StopsBeforeHttpWithoutReplacingBytes()
    {
        using var files = new LocalTestDirectory();
        using var handler = new LocalHttpFixture((_, _) => throw new InvalidOperationException("No HTTP call was allowed."));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var server = new LocalMcpServer(new LocalInvocationClient(new LocalHttp(client, TimeProvider.System), "onboarding", "agent", files.Private));
        using var input = new MemoryStream([0xff, 0x0a]);
        using var output = new MemoryStream();
        await Should.ThrowAsync<DecoderFallbackException>(() => server.RunAsync(input, output, TestContext.Current.CancellationToken));
        handler.Requests.ShouldBeEmpty();
        output.Length.ShouldBe(0);
        Directory.Exists(files.Private).ShouldBeFalse();
    }
    [Fact]
    public async Task RunAsync_DiscoveryAndForbiddenTool_ExposesOnlyFourBusinessTools()
    {
        using var files = new LocalTestDirectory();
        using var handler = new LocalHttpFixture((_, _) => throw new InvalidOperationException("No HTTP call was allowed."));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var server = new LocalMcpServer(new LocalInvocationClient(new LocalHttp(client, TimeProvider.System), "onboarding", "agent", files.Private));
        using var input = new StringReader("""
            {"jsonrpc":"2.0","id":1,"method":"initialize"}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/list"}
            {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"approve","arguments":{}}}
            """);
        using var output = new StringWriter();
        (await server.RunAsync(input, output, TestContext.Current.CancellationToken)).ShouldBe(0);
        var responses = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
        responses[1]["result"]!["tools"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).Order()
            .ShouldBe(BusinessTools);
        responses[2]["result"]!["isError"]!.GetValue<bool>().ShouldBeTrue();
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_OversizedLine_StopsBeforeParsingOrHttp()
    {
        using var files = new LocalTestDirectory();
        using var handler = new LocalHttpFixture((_, _) => throw new InvalidOperationException("No HTTP call was allowed."));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var server = new LocalMcpServer(new LocalInvocationClient(new LocalHttp(client, TimeProvider.System), "onboarding", "agent", files.Private));
        using var input = new StringReader(new string('x', LocalHttp.MaxBytes + 1));
        using var output = new StringWriter();
        await Should.ThrowAsync<ArgumentException>(() => server.RunAsync(input, output, TestContext.Current.CancellationToken));
        output.ToString().ShouldBeEmpty();
        handler.Requests.ShouldBeEmpty();
    }
}
