using System.Text;
using System.Text.Json.Nodes;

namespace Weave.Cli.Tests;

public sealed class LocalMcpCancellationBoundaryTests
{
    private const string ReadRequest = "{\"jsonrpc\":\"2.0\",\"id\":\"read\",\"method\":\"tools/call\",\"params\":{\"name\":\"read_document\",\"arguments\":{\"path\":\"owned-document.md\"}}}\n";

    [Fact]
    public async Task RunAsync_HttpTimeoutWithLiveCaller_ReturnsUnconfirmedToolErrorWithoutLeakingDetails()
    {
        using var handler = new LocalHttpFixture((_, _) => throw new TaskCanceledException("synthetic-sensitive-http-detail"));
        using var fixture = new LocalMcpStreamFixture(handler);

        var replies = await fixture.RunAsync(LocalMcpStreamFixture.Handshake + ReadRequest, TestContext.Current.CancellationToken);

        replies.Length.ShouldBe(2);
        replies[1]["id"].ShouldNotBeNull().GetValue<string>().ShouldBe("read");
        var result = replies[1]["result"].ShouldNotBeNull();
        result["isError"].ShouldNotBeNull().GetValue<bool>().ShouldBeTrue();
        result["content"].ShouldNotBeNull().AsArray().ShouldHaveSingleItem().ShouldNotBeNull()["text"].ShouldNotBeNull()
            .GetValue<string>().ShouldBe("Request not confirmed. Check the original UUID and current authority. Do not retry with a new UUID or replace the original content.");
        handler.Requests.ShouldHaveSingleItem().Method.ShouldBe("POST");
        Encoding.UTF8.GetString(fixture.Output.ToArray()).ShouldNotContain("synthetic-sensitive-http-detail");
    }

    [Fact]
    public async Task RunAsync_CallerCancelsObservedRequest_PropagatesCancellationWithoutFabricatingToolReply()
    {
        using var handler = new LocalMcpPendingHandler();
        using var fixture = new LocalMcpStreamFixture(handler);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = fixture.RunAsync(LocalMcpStreamFixture.Handshake + ReadRequest, cancellation.Token);
        try
        {
            await handler.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            running.IsCompleted.ShouldBeFalse();
            cancellation.Cancel();

            await Should.ThrowAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            var lines = Encoding.UTF8.GetString(fixture.Output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            JsonNode.Parse(lines.ShouldHaveSingleItem()).ShouldNotBeNull()["id"].ShouldNotBeNull().GetValue<string>().ShouldBe("init");
        }
        finally
        {
            cancellation.Cancel();
            var failure = await Record.ExceptionAsync(async () => await running.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
            failure.ShouldBeAssignableTo<OperationCanceledException>();
        }
    }
}
