using System.Text;
using System.Text.Json.Nodes;
using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

internal sealed class LocalMcpStreamFixture(HttpMessageHandler handler) : IDisposable
{
    private readonly LocalTestDirectory _files = new();
    private readonly HttpClient _client = new(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
    public MemoryStream Output { get; } = new();
    public const string Handshake = "{\"jsonrpc\":\"2.0\",\"id\":\"init\",\"method\":\"initialize\"}\n"
        + "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n";

    public async Task<JsonObject[]> RunAsync(string messages, CancellationToken ct)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(messages));
        var server = new LocalMcpServer(new LocalInvocationClient(new LocalHttp(_client, TimeProvider.System),
            "onboarding", "synthetic-agent-capability", _files.Private));
        (await server.RunAsync(input, Output, ct)).ShouldBe(0);
        var text = Encoding.UTF8.GetString(Output.ToArray());
        text.ShouldNotStartWith("\ufeff");
        Directory.Exists(_files.Private).ShouldBeFalse();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line).ShouldBeOfType<JsonObject>()).ToArray();
    }

    public void Dispose()
    {
        Output.Dispose();
        _client.Dispose();
        _files.Dispose();
    }
}
