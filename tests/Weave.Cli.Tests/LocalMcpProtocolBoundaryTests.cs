namespace Weave.Cli.Tests;

public sealed class LocalMcpProtocolBoundaryTests
{
    [Theory]
    [InlineData("not-json-sensitive-marker")]
    [InlineData("[]")]
    [InlineData("{\"jsonrpc\":\"1.0\",\"id\":1,\"method\":\"ping\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":9}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":true,\"method\":\"ping\"}")]
    public async Task RunAsync_InvalidEnvelope_ReturnsBoundedErrorThenContinuesWithCorrelatedPing(string invalid)
    {
        using var handler = new LocalHttpFixture((_, _) => throw new InvalidOperationException("Invalid protocol must not reach HTTP."));
        using var fixture = new LocalMcpStreamFixture(handler);

        var replies = await fixture.RunAsync(invalid + "\n{\"jsonrpc\":\"2.0\",\"id\":\"probe\",\"method\":\"ping\"}\n",
            TestContext.Current.CancellationToken);

        replies.Length.ShouldBe(2);
        replies[0]["error"].ShouldNotBeNull()["code"].ShouldNotBeNull().GetValue<int>().ShouldBe(-32600);
        replies[0]["error"].ShouldNotBeNull()["message"].ShouldNotBeNull().GetValue<string>()
            .ShouldBe("Invalid or unsupported bounded MCP request.");
        replies[1]["id"].ShouldNotBeNull().GetValue<string>().ShouldBe("probe");
        replies[1]["result"].ShouldNotBeNull().AsObject().ShouldBeEmpty();
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_InitializationOrderAndUnsupportedMethod_RequiresReadyNotificationAndRecovers()
    {
        using var handler = new LocalHttpFixture((_, _) => throw new InvalidOperationException("Protocol control messages must not reach HTTP."));
        using var fixture = new LocalMcpStreamFixture(handler);
        const string messages = """
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":1,"method":"tools/list"}
            {"jsonrpc":"2.0","id":2,"method":"initialize"}
            {"jsonrpc":"2.0","id":3,"method":"tools/list"}
            {"jsonrpc":"2.0","method":"notifications/ignored"}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":4,"method":"operator/approve"}
            {"jsonrpc":"2.0","id":5,"method":"tools/list"}
            """;

        var replies = await fixture.RunAsync(messages, TestContext.Current.CancellationToken);

        replies.Select(reply => reply["id"].ShouldNotBeNull().GetValue<int>()).ShouldBe([1, 2, 3, 4, 5]);
        int[] deniedReplies = [0, 2, 3];
        foreach (var index in deniedReplies)
            replies[index]["error"].ShouldNotBeNull()["code"].ShouldNotBeNull().GetValue<int>().ShouldBe(-32600);
        replies[1]["result"].ShouldNotBeNull()["protocolVersion"].ShouldNotBeNull().GetValue<string>().ShouldBe("2024-11-05");
        replies[4]["result"].ShouldNotBeNull()["tools"].ShouldNotBeNull().AsArray()
            .Select(tool => tool.ShouldNotBeNull()["name"].ShouldNotBeNull().GetValue<string>()).ShouldContain("get_status");
        handler.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":9,\"arguments\":{}}")]
    [InlineData("{\"name\":\"get_status\",\"arguments\":{\"invocation_id\":9}}")]
    [InlineData("{\"name\":\"get_status\",\"arguments\":{\"invocation_id\":\"82c07b3d2a3646e88f2f0b8db07c452b\",\"operator_key\":\"injected-authority\"}}")]
    public async Task RunAsync_InvalidToolArguments_RejectsBeforeHttpWithoutEchoingInjectedAuthority(string parameters)
    {
        using var handler = new LocalHttpFixture((_, _) => throw new InvalidOperationException("Invalid tool arguments must not reach HTTP."));
        using var fixture = new LocalMcpStreamFixture(handler);
        var call = "{\"jsonrpc\":\"2.0\",\"id\":\"call\",\"method\":\"tools/call\",\"params\":" + parameters + "}\n";

        var replies = await fixture.RunAsync(LocalMcpStreamFixture.Handshake + call, TestContext.Current.CancellationToken);

        replies.Length.ShouldBe(2);
        replies[1]["id"].ShouldNotBeNull().GetValue<string>().ShouldBe("call");
        var result = replies[1]["result"].ShouldNotBeNull();
        result["isError"].ShouldNotBeNull().GetValue<bool>().ShouldBeTrue();
        result["content"].ShouldNotBeNull().AsArray().ShouldHaveSingleItem().ShouldNotBeNull()["text"].ShouldNotBeNull()
            .GetValue<string>().ShouldBe("Use one of the four business tools with its exact string arguments.");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_MultibyteLineWithinCharacterLimit_RejectsUtf8ByteOverflowBeforeHttp()
    {
        using var handler = new LocalHttpFixture((_, _) => throw new InvalidOperationException("Oversized bytes must not reach HTTP."));
        using var fixture = new LocalMcpStreamFixture(handler);

        var failure = await Should.ThrowAsync<ArgumentException>(() => fixture.RunAsync(new string('界', 400000) + "\n",
            TestContext.Current.CancellationToken));

        failure.Message.ShouldBe("MCP input exceeds the client limit.");
        fixture.Output.Length.ShouldBe(0);
        handler.Requests.ShouldBeEmpty();
    }
}
