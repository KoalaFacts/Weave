using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Weave.Cli.Tests;

internal sealed class LocalHttpFixture(Func<HttpRequestMessage, JsonObject?, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(string Method, string Path, JsonObject? Body, bool Operator)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct))!.AsObject();
        Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body, request.Headers.Contains("X-Weave-Operator-Key")));
        return respond(request, body);
    }

    public static HttpResponseMessage Response(int status, JsonObject? body = null) => new((HttpStatusCode)status)
    {
        Content = new StringContent(body?.ToJsonString() ?? "", Encoding.UTF8, "application/json")
    };
}
