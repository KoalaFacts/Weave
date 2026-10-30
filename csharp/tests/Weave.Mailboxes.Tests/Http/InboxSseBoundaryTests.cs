using System.Net;
using System.Net.Sockets;
using System.Text;
using Weave.Mailbox.Host;
using static Weave.Mailboxes.Tests.Storage.MailboxTestContext;

namespace Weave.Mailboxes.Tests.Http;

[Trait("Category", "Integration")]
public sealed class InboxSseBoundaryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Events_LiveBlockOrExpiry_StopsReplayBeforeNextEvent(bool block)
    {
        await using var context = new MailboxHttpContext(o => o with { StreamLifetime = TimeSpan.FromSeconds(3) });
        var relation = context.Data.Connect();
        var payload = context.Data.Payload(lifetime: TimeSpan.FromSeconds(1));
        Require(context.Data.Store.Send(Alice, context.Data.Envelope(relation, payload), MailboxHttpContext.Ct));
        await context.Start();
        using var response = await context.Send("GET", "/v1/inbox/events", "bob", completion: HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(MailboxHttpContext.Ct));
        string? line;
        do
        { line = await reader.ReadLineAsync(MailboxHttpContext.Ct); line.ShouldNotBeNull(); } while (!line.StartsWith("data: ", StringComparison.Ordinal));
        if (block)
            Require(context.Data.Store.SetBlocked(Bob, Alice.MailboxId, relation.Generation, true, MailboxHttpContext.Ct));
        else
            context.Data.Clock.Advance(TimeSpan.FromSeconds(1));
        while ((line = await reader.ReadLineAsync(MailboxHttpContext.Ct)) is not null)
            line.StartsWith("data: ", StringComparison.Ordinal).ShouldBeFalse();
        context.Data.Store.ReadPending(Bob, null, 10, MailboxHttpContext.Ct).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Events_CancelledTcpConnection_ReleasesSlotWithoutAck()
    {
        await using var context = new MailboxHttpContext(o => o with { MaximumStreamsPerMailbox = 1 });
        var relation = context.Data.Connect();
        var payload = context.Data.Payload();
        Require(context.Data.Store.Send(Alice, context.Data.Envelope(relation, payload), MailboxHttpContext.Ct));
        await context.Start();
        using (var socket = await OpenRaw(context))
        { socket.Client.LingerState = new(true, 0); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(MailboxHttpContext.Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        HttpResponseMessage response;
        do
        {
            await Task.Delay(50, deadline.Token);
            response = await context.Send("GET", "/v1/inbox/events", "bob", completion: HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                response.Dispose();
        } while (response.StatusCode == HttpStatusCode.TooManyRequests);
        using (response)
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        context.Data.Store.ReadPending(Bob, null, 10, MailboxHttpContext.Ct).Items.Length.ShouldBe(1);
    }

    [Fact]
    public async Task Events_SlowTcpReader_WriteDeadlineClosesAndFreesSlot()
    {
        await using var context = new MailboxHttpContext(o => o with { MaximumStreamsPerMailbox = 1, StreamWriteTimeout = TimeSpan.FromSeconds(1) });
        var relation = context.Data.Connect();
        for (var i = 0; i < 64; i++)
        {
            var now = context.Data.Clock.GetUtcNow();
            var payload = new MailboxPayload(Guid.CreateVersion7(now), now, now.AddMinutes(5), "opaque", new byte[65536]);
            Require(context.Data.Store.Send(Alice, context.Data.Envelope(relation, payload), MailboxHttpContext.Ct));
        }
        await context.Start();
        using var socket = await OpenRaw(context);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(MailboxHttpContext.Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        HttpResponseMessage response;
        do
        {
            await Task.Delay(100, deadline.Token);
            response = await context.Send("GET", "/v1/inbox/events", "bob", completion: HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                response.Dispose();
        } while (response.StatusCode == HttpStatusCode.TooManyRequests);
        using (response)
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        context.Data.Store.ReadPending(Bob, null, 100, MailboxHttpContext.Ct).Items.Length.ShouldBe(64);
        lock (context.Logs)
            string.Join('\n', context.Logs).ShouldNotContain("unhandled exception");
    }

    [Fact]
    public async Task Events_GlobalStreamBound_RejectsAnotherMailboxWithoutBlockingAck()
    {
        await using var context = new MailboxHttpContext(o => o with { MaximumStreams = 1 });
        var relation = context.Data.Connect();
        var payload = context.Data.Payload();
        Require(context.Data.Store.Send(Alice, context.Data.Envelope(relation, payload), MailboxHttpContext.Ct));
        await context.Start();
        using var stream = await context.Send("GET", "/v1/inbox/events", "bob", completion: HttpCompletionOption.ResponseHeadersRead);
        stream.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var refused = await context.Send("GET", "/v1/inbox/events", "alice", completion: HttpCompletionOption.ResponseHeadersRead);
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        var ack = await MailboxHttpContext.Json(await context.Send("POST", $"/v1/inbox/{payload.MessageId}/ack", "bob", body: new { senderMailboxId = "alice" }));
        ack.GetProperty("state").GetString().ShouldBe("acknowledged");
    }

    private static async Task<TcpClient> OpenRaw(MailboxHttpContext context)
    {
        var address = context.Client.BaseAddress!;
        var client = new TcpClient { ReceiveBufferSize = 1024 };
        await client.ConnectAsync(address.Host, address.Port, MailboxHttpContext.Ct);
        var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes($"GET /v1/inbox/events HTTP/1.1\r\nHost: {address.Host}:{address.Port}\r\n{MailboxControlAuthentication.HeaderName}: {context.Secret("bob")}\r\n\r\n");
        await stream.WriteAsync(request, MailboxHttpContext.Ct);
        var headers = new StringBuilder();
        var one = new byte[1];
        while (!headers.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            (await stream.ReadAsync(one, MailboxHttpContext.Ct)).ShouldBe(1);
            headers.Append((char)one[0]);
            headers.Length.ShouldBeLessThan(8192);
        }
        headers.ToString().ShouldStartWith("HTTP/1.1 200");
        return client;
    }
}
