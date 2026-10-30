using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using static Weave.Mailboxes.Tests.Storage.MailboxTestContext;

namespace Weave.Mailboxes.Tests.Http;

[Trait("Category", "Integration")]
public sealed class InboxSseTests
{
    [Fact]
    public async Task Events_ReconnectWithLastEventId_ReplaysUntilAuthenticatedAck()
    {
        await using var context = new MailboxHttpContext(o => o with { StreamLifetime = TimeSpan.FromSeconds(3) });
        var relation = context.Data.Connect();
        var payload = context.Data.Payload();
        Require(context.Data.Store.Send(Alice, context.Data.Envelope(relation, payload), Ct));
        await context.Start();
        using (var first = await Events(context))
        {
            var message = await ReadEvent(first);
            message.GetProperty("envelope").GetProperty("payload").GetProperty("messageId").GetGuid().ShouldBe(payload.MessageId);
        }
        await context.Restart();
        using (var second = await context.Send("GET", "/v1/inbox/events", "bob", completion: HttpCompletionOption.ResponseHeadersRead, lastEventId: payload.MessageId.ToString()))
        {
            var message = await ReadEvent(second);
            message.GetProperty("envelope").GetProperty("payload").GetProperty("messageId").GetGuid().ShouldBe(payload.MessageId);
            context.Data.Scalar("SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL").ShouldBe(1);
        }
        await LoseAckResponse(context, payload.MessageId);
        var stable = context.Data.Store.GetReceipt(Alice, payload.MessageId, Ct).ShouldNotBeNull();
        stable.State.ShouldBe(MailboxReceiptState.Acknowledged);
        context.Data.Clock.Advance(TimeSpan.FromSeconds(1));
        await context.Restart();
        var positive = context.Data.Payload("live positive after lost ACK");
        Require(context.Data.Store.Send(Alice, context.Data.Envelope(relation, positive), Ct));
        using var final = await Events(context);
        var events = await ObserveToCompletion(final);
        events.Count.ShouldBeGreaterThanOrEqualTo(2);
        events.ShouldAllBe(id => id == positive.MessageId);
        var repeated = await MailboxHttpContext.Json(await context.Send("POST", $"/v1/inbox/{payload.MessageId}/ack", "bob", new { senderMailboxId = "alice" }));
        repeated.GetProperty("state").GetString().ShouldBe("acknowledged");
        ((DateTimeOffset?)repeated.GetProperty("terminalAt").GetDateTimeOffset()).ShouldBe(stable.TerminalAt);
        context.Data.Store.GetReceipt(Alice, payload.MessageId, Ct).ShouldBe(stable);
        context.Data.Scalar("SELECT count(*) FROM mailbox_messages WHERE id='" + payload.MessageId + "' AND payload IS NOT NULL").ShouldBe(0);
    }

    [Fact]
    public async Task Events_MoreThanPage_ReplaysEarlierUnacknowledgedAfterCursorReconnect()
    {
        await using var context = new MailboxHttpContext();
        var relation = context.Data.Connect();
        for (var i = 0; i < 12; i++)
            Require(context.Data.Store.Send(Alice, context.Data.Envelope(relation), Ct));
        await context.Start();
        var page = await MailboxHttpContext.Json(await context.Send("GET", "/v1/inbox?limit=10", "bob"));
        page.GetProperty("items").GetArrayLength().ShouldBe(10);
        var cursor = page.GetProperty("nextCursor").GetString();
        cursor.ShouldNotBeNull();
        var tail = await MailboxHttpContext.Json(await context.Send("GET", "/v1/inbox?afterCursor=" + Uri.EscapeDataString(cursor), "bob"));
        tail.GetProperty("items").GetArrayLength().ShouldBe(2);
        using var response = await context.Send("GET", "/v1/inbox/events?afterCursor=" + Uri.EscapeDataString(cursor), "bob", completion: HttpCompletionOption.ResponseHeadersRead, lastEventId: cursor);
        var first = await ReadEvent(response);
        first.GetProperty("envelope").GetProperty("payload").GetProperty("messageId").GetGuid().ShouldBe(page.GetProperty("items")[0].GetProperty("envelope").GetProperty("payload").GetProperty("messageId").GetGuid());
        context.Data.Store.ReadPending(Bob, null, 100, Ct).Items.Length.ShouldBe(12);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Events_ExpiredOrBlockedBeforeEmission_NoPayload(bool block)
    {
        await using var context = new MailboxHttpContext(o => o with { StreamLifetime = TimeSpan.FromSeconds(3) });
        var relation = context.Data.Connect();
        var card = context.Data.Card();
        var payload = context.Data.Payload(lifetime: TimeSpan.FromSeconds(1));
        Require(context.Data.Store.Send(Alice, context.Data.Envelope(relation, payload), Ct));
        if (block)
            Require(context.Data.Store.SetBlocked(Bob, Alice.MailboxId, relation.Generation, true, Ct));
        else
            context.Data.Clock.Advance(TimeSpan.FromSeconds(1));
        var eve = context.Data.Request(card, Eve);
        var accepted = Require(context.Data.Store.DecideContact(Bob, new(eve.Request.Locator, eve.Generation, Weave.Contacts.ContactStatus.Accepted), Ct));
        Require(context.Data.Store.Acknowledge(Bob, Eve.MailboxId, eve.Request.RequestMessageId!.Value, Ct));
        var positive = context.Data.Payload("independent live positive");
        Require(context.Data.Store.Send(Eve, context.Data.Envelope(accepted, positive), Ct));
        await context.Start();
        using var response = await Events(context);
        var events = await ObserveToCompletion(response);
        events.Count.ShouldBeGreaterThanOrEqualTo(2);
        events.ShouldAllBe(id => id == positive.MessageId);
    }

    [Fact]
    public async Task Events_StreamSlotsBounded_AckRemainsAvailable()
    {
        await using var context = new MailboxHttpContext();
        var relation = context.Data.Connect();
        var payload = context.Data.Payload();
        Require(context.Data.Store.Send(Alice, context.Data.Envelope(relation, payload), Ct));
        await context.Start();
        using var first = await Events(context);
        using var second = await Events(context);
        (await context.Send("GET", "/v1/inbox/events", "bob", completion: HttpCompletionOption.ResponseHeadersRead)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        await MailboxHttpContext.Json(await context.Send("POST", $"/v1/inbox/{payload.MessageId}/ack", "bob", body: new { senderMailboxId = "alice" }));
    }

    [Fact]
    public async Task Events_ControlRevoked_ClosesAndDeniesReconnect()
    {
        await using var context = new MailboxHttpContext();
        await context.Start();
        using var response = await Events(context);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
        (await reader.ReadLineAsync(Ct)).ShouldBe(": pending");
        context.Authentication.Revoke(context.Options.Credentials.Single(c => c.MailboxId == "bob").Sha256);
        while (await reader.ReadLineAsync(Ct) is not null)
        { }
        (await context.Send("GET", "/v1/inbox/events", "bob", completion: HttpCompletionOption.ResponseHeadersRead)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Events_ShortConfiguredLifetime_ClosesWithoutAck()
    {
        await using var context = new MailboxHttpContext(o => o with { StreamLifetime = TimeSpan.FromSeconds(1) });
        var relation = context.Data.Connect();
        Require(context.Data.Store.Send(Alice, context.Data.Envelope(relation), Ct));
        await context.Start();
        using var response = await Events(context);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
        while (await reader.ReadLineAsync(Ct) is not null)
        { }
        context.Data.Store.ReadPending(Bob, null, 10, Ct).Items.Length.ShouldBe(1);
    }

    private static async Task<HttpResponseMessage> Events(MailboxHttpContext context)
    {
        var response = await context.Send("GET", "/v1/inbox/events", "bob", completion: HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        return response;
    }
    private static async Task<List<Guid>> ObserveToCompletion(HttpResponseMessage response)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
        var events = new List<Guid>();
        string? line;
        while ((line = await reader.ReadLineAsync(Ct)) is not null)
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(line[6..]);
                events.Add(doc.RootElement.GetProperty("envelope").GetProperty("payload").GetProperty("messageId").GetGuid());
            }
        return events;
    }
    private static async Task LoseAckResponse(MailboxHttpContext context, Guid id)
    {
        var address = context.Client.BaseAddress!;
        using var socket = new TcpClient();
        await socket.ConnectAsync(address.Host, address.Port, Ct);
        var body = "{\"senderMailboxId\":\"alice\"}";
        var request = Encoding.ASCII.GetBytes($"POST /v1/inbox/{id:D}/ack HTTP/1.1\r\nHost: {address.Host}:{address.Port}\r\nX-Weave-Mailbox-Control: {context.Secret("bob")}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n{body}");
        await socket.GetStream().WriteAsync(request, Ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (context.Data.Store.GetReceipt(Alice, id, Ct)?.State != MailboxReceiptState.Acknowledged)
            await Task.Delay(10, deadline.Token);
        // The recipient observes durable ACK but discards the TCP response without reading any response byte.
        socket.Client.LingerState = new(true, 0);
    }
    private static async Task<JsonElement> ReadEvent(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
        string? line;
        while ((line = await reader.ReadLineAsync(Ct)) is not null)
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            { using var doc = JsonDocument.Parse(line[6..]); return doc.RootElement.Clone(); }
        throw new Xunit.Sdk.XunitException("Expected an inbox event before the stream closed.");
    }
    private static CancellationToken Ct => MailboxHttpContext.Ct;
}
