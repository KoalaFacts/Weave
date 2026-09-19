using System.Net;
using System.Text;

namespace Weave.Agents.Tests.Pipeline.Providers.Anthropic;

/// <summary>Captures the last request and returns a canned response or throws.</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private HttpStatusCode _status = HttpStatusCode.OK;
    private string _body = "{}";
    private Exception? _exception;

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    public StubHttpMessageHandler Returns(HttpStatusCode status, string body)
    {
        _status = status;
        _body = body;
        _exception = null;
        return this;
    }

    public StubHttpMessageHandler Throws(Exception exception)
    {
        _exception = exception;
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (_exception is not null)
            throw _exception;

        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json")
        };
    }
}
