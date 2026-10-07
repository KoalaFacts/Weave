using System.Net;
using System.Text;
using static Weave.Mailboxes.Tests.Storage.MailboxTestContext;

namespace Weave.Mailboxes.Tests.Http;

[Trait("Category", "Integration")]
public sealed class MailboxHttpTests
{
    [Fact]
    public async Task Send_AuthorizedSender_ReceiptAndOwnedInboxAcrossRestart()
    {
        await using var context = new MailboxHttpContext();
        var relation = context.Data.Connect();
        await context.Start();
        var payload = context.Data.Payload("opaque body https://127.0.0.1:1/never-fetch");
        var body = context.Message(relation, payload);
        var admitted = await MailboxHttpContext.Json(await context.Send("POST", "/v1/messages", body: body));
        admitted.GetProperty("state").GetString().ShouldBe("pending");
        admitted.GetProperty("senderMailboxId").GetString().ShouldBe("alice");
        await context.Restart();
        var inbox = await MailboxHttpContext.Json(await context.Send("GET", "/v1/inbox", "bob"));
        inbox.GetProperty("items").GetArrayLength().ShouldBe(1);
        Encoding.UTF8.GetString(inbox.GetProperty("items")[0].GetProperty("envelope").GetProperty("payload").GetProperty("bytes").GetBytesFromBase64()).ShouldBe("opaque body https://127.0.0.1:1/never-fetch");
        var receipt = await MailboxHttpContext.Json(await context.Send("GET", $"/v1/outbox/{payload.MessageId}"));
        receipt.TryGetProperty("payload", out _).ShouldBeFalse();
        (await context.Send("GET", $"/v1/outbox/{payload.MessageId}", "eve")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await context.Send("GET", "/v1/inbox", "eve")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var retry = await MailboxHttpContext.Json(await context.Send("POST", "/v1/messages", body: body));
        retry.GetProperty("messageId").GetGuid().ShouldBe(payload.MessageId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid-synthetic-control")]
    public async Task Inbox_MissingOrInvalidControl_Unauthorized(string? owner)
    {
        await using var context = new MailboxHttpContext(); await context.Start();
        (await context.Send("GET", "/v1/inbox", owner)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ack_RecipientOnly_IdempotentDeletionAndNoExistenceLeak()
    {
        await using var context = new MailboxHttpContext(); var relation = context.Data.Connect();
        var payload = context.Data.Payload(); Require(context.Data.Store.Send(Alice, context.Data.Envelope(relation, payload), Ct));
        await context.Start();
        var path = $"/v1/inbox/{payload.MessageId}/ack";
        (await context.Send("POST", path, "eve", body: new { senderMailboxId = "alice" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await context.Send("POST", $"/v1/inbox/{Guid.NewGuid()}/ack", "eve", body: new { senderMailboxId = "alice" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await context.Send("POST", path, null, body: new { senderMailboxId = "alice" })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var ack = await MailboxHttpContext.Json(await context.Send("POST", path, "bob", body: new { senderMailboxId = "alice" }));
        ack.GetProperty("state").GetString().ShouldBe("acknowledged");
        var again = await MailboxHttpContext.Json(await context.Send("POST", path, "bob", body: new { senderMailboxId = "alice" })); again.GetRawText().ShouldBe(ack.GetRawText());
        context.Data.Scalar("SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL").ShouldBe(0);
        (await MailboxHttpContext.Json(await context.Send("GET", "/v1/inbox", "bob"))).GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Send_UntrustedIdentityFields_InvalidWithoutAdmission()
    {
        await using var context = new MailboxHttpContext(); var relation = context.Data.Connect(); await context.Start();
        var body = new { version = 1, senderMailboxId = "bob", recipientMailboxId = "bob", contactGeneration = relation.Generation, payload = MailboxHttpContext.Payload(context.Data.Payload()) };
        (await context.Send("POST", "/v1/messages", body: body)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        context.Data.Store.ReadPending(Bob, null, 10, Ct).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Send_OversizedDecodedPayload_Invalid()
    {
        await using var context = new MailboxHttpContext(); var relation = context.Data.Connect(); await context.Start();
        var now = context.Data.Clock.GetUtcNow();
        var body = new { version = 1, recipientMailboxId = "bob", contactGeneration = relation.Generation,
            payload = new { messageId = Guid.CreateVersion7(now), createdAt = now, expiresAt = now.AddMinutes(1), payloadEncoding = "opaque", bytes = Convert.ToBase64String(new byte[65537]) } };
        (await context.Send("POST", "/v1/messages", body: body)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        context.Data.Scalar("SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL").ShouldBe(0);
    }

    [Fact]
    public async Task Send_RequestBodyAboveCap_PayloadTooLarge()
    {
        await using var context = new MailboxHttpContext(); await context.Start();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = new StringContent(new string(' ', 131073), Encoding.UTF8, "application/json") };
        request.Headers.Add(Weave.Mailbox.Host.MailboxControlAuthentication.HeaderName, context.Secret("alice"));
        (await context.Client.SendAsync(request, Ct)).StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Theory]
    [InlineData("limit=11")]
    [InlineData("limit=0")]
    [InlineData("limit=oops")]
    [InlineData("afterCursor=bad")]
    public async Task Inbox_InvalidPagination_Invalid(string query)
    {
        await using var context = new MailboxHttpContext(); await context.Start();
        (await context.Send("GET", "/v1/inbox?" + query, "bob")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Send_CapacityPressure_AckStillReleasesCapacity()
    {
        await using var context = new MailboxHttpContext(storage: o => o with { MaximumPendingMessagesPerMailbox = 1 });
        var relation = context.Data.Connect(); await context.Start(); var payload = context.Data.Payload();
        await MailboxHttpContext.Json(await context.Send("POST", "/v1/messages", body: context.Message(relation, payload)));
        (await context.Send("POST", "/v1/messages", body: context.Message(relation))).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        await MailboxHttpContext.Json(await context.Send("POST", $"/v1/inbox/{payload.MessageId}/ack", "bob", body: new { senderMailboxId = "alice" }));
        await MailboxHttpContext.Json(await context.Send("POST", "/v1/messages", body: context.Message(relation)));
    }

    [Fact]
    public async Task Send_SecretsAndBodies_AbsentFromLogsAndResponsesNotCached()
    {
        await using var context = new MailboxHttpContext(); var relation = context.Data.Connect(); await context.Start();
        var marker = "PRIVATE-BODY-" + Guid.NewGuid();
        var response = await context.Send("POST", "/v1/messages", body: context.Message(relation, context.Data.Payload(marker)));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        lock (context.Logs)
        {
            var log = string.Join('\n', context.Logs); log.ShouldNotContain(marker); log.ShouldNotContain(context.Secret("alice"));
        }
    }
    private static CancellationToken Ct => MailboxHttpContext.Ct;
}
