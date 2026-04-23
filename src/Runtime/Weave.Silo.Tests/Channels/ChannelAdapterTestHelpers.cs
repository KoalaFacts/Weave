using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Weave.Silo.Tests.Channels;

/// <summary>
/// Shared test utilities for channel adapter tests.
///
/// All channel adapters (Slack, Discord, Telegram, Teams, Email) follow
/// the same shape: read config keys, POST JSON to a webhook URL via an
/// injected <see cref="HttpClient"/>. Tests are structurally identical
/// so the stub handler and payload-assertion helpers live here.
///
/// Pattern matches <c>src/Security/Weave.Security.Tests/VaultSecretProviderTests.cs</c>
/// and <c>src/Tools/Weave.Tools.Tests/DaprToolConnectorTests.cs</c> — StubHandler
/// captures the last request, tests make assertions on it.
/// </summary>
internal static class ChannelAdapterTestHelpers
{
    /// <summary>
    /// Captures the last outgoing request and returns a configurable
    /// response. Default response is HTTP 200 with an empty body, which
    /// is what every real webhook returns on success.
    /// </summary>
    internal sealed class CapturingHandler(HttpStatusCode statusCode = HttpStatusCode.OK, string responseBody = "") : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(ct);
            }
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    internal static void AssertJsonPayload(string? body, Action<JsonElement> assertions)
    {
        body.ShouldNotBeNullOrEmpty("adapter posted an empty body");
        using var doc = JsonDocument.Parse(body!);
        assertions(doc.RootElement);
    }
}
