using Weave.Contacts;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Weave.Mailbox.Host;
using Weave.Mailboxes.Tests.Storage;

namespace Weave.Mailboxes.Tests.Http;

internal sealed class MailboxHttpContext : IAsyncDisposable
{
    private readonly string _alice = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _bob = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _eve = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private WebApplication? _app;
    public MailboxHttpContext(Func<MailboxHostOptions, MailboxHostOptions>? configure = null,
        Func<MailboxOptions, MailboxOptions>? storage = null)
    {
        Data = new(storage);
        var options = new MailboxHostOptions { Credentials = [Credential("alice", _alice), Credential("bob", _bob), Credential("eve", _eve)] };
        Options = configure is null ? options : configure(options);
    }
    public MailboxTestContext Data { get; }
    public MailboxHostOptions Options { get; }
    public MailboxControlAuthentication Authentication { get; } = new();
    public HttpClient Client { get; private set; } = null!;
    public List<string> Logs { get; } = [];
    public static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static MailboxControlCredential Credential(string mailbox, string secret) => new(mailbox,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))));
    public async Task Start()
    {
        _app = MailboxHost.Create(Options, Data.Restart(), Data.Clock, Authentication, builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new MailboxTestLogProvider(Logs));
        });
        await _app.StartAsync(Ct);
        Client = new() { BaseAddress = new(_app.Urls.Single()), Timeout = TimeSpan.FromSeconds(15) };
    }
    public async Task Restart()
    {
        Client.Dispose();
        await _app!.StopAsync(Ct);
        await _app.DisposeAsync();
        await Start();
    }
    public string Secret(string owner) => owner switch { "alice" => _alice, "bob" => _bob, "eve" => _eve, _ => owner };
    public async Task<HttpResponseMessage> Send(string method, string path, string? owner = "alice", object? body = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, string? lastEventId = null)
    {
        using var request = new HttpRequestMessage(new(method), path);
        if (owner is not null) request.Headers.Add(MailboxControlAuthentication.HeaderName, Secret(owner));
        if (lastEventId is not null) request.Headers.Add("Last-Event-ID", lastEventId);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await Client.SendAsync(request, completion, Ct);
    }
    public static object Payload(MailboxPayload payload) => new
    {
        messageId = payload.MessageId, createdAt = payload.CreatedAt, expiresAt = payload.ExpiresAt,
        payloadEncoding = payload.PayloadEncoding, bytes = Convert.ToBase64String(payload.Bytes.AsSpan())
    };
    public object Message(ContactRelation relation, MailboxPayload? payload = null) => new
    {
        version = 1, recipientMailboxId = "bob", contactGeneration = relation.Generation,
        payload = Payload(payload ?? Data.Payload())
    };
    public static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        response.StatusCode.ShouldBe(expected);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }
    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (_app is not null) { await _app.StopAsync(Ct); await _app.DisposeAsync(); }
        Data.Dispose();
    }
}
