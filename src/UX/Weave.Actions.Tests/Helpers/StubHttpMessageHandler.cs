using System.Net;
using System.Net.Http;

namespace Weave.Actions.Tests.Helpers;

/// <summary>
/// Test-only HTTP message handler that returns a canned response (or throws a
/// canned exception) for whatever request the action sends. Used in place of
/// a real network call when driving an action under test. Captures the last
/// request's URL and method so tests can assert what the action asked for
/// without inlining a fresh <see cref="HttpResponseMessage"/> lambda (which
/// CodeQL flags as an undisposed local).
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handle;

    public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle)
    {
        _handle = handle;
    }

    public Uri? LastRequestUri { get; private set; }

    public HttpMethod? LastRequestMethod { get; private set; }

    public static StubHttpMessageHandler Returns(HttpStatusCode status, string? body = null)
        => new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = body is null ? new StringContent(string.Empty) : new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        }));

    public static StubHttpMessageHandler Throws(Exception ex)
        => new((_, _) => throw ex);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        LastRequestMethod = request.Method;
        return await _handle(request, cancellationToken);
    }
}
