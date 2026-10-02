using System.Text;
using System.Text.Json.Nodes;
using Shouldly;
using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

public sealed class LocalMcpServerTests
{
    private static readonly string[] BusinessTools = ["get_status", "read_document", "resume_write", "submit_write"];

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
