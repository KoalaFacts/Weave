using System.Net;
using Weave.Contacts;
using static Weave.Mailboxes.Tests.Storage.MailboxTestContext;

namespace Weave.Mailboxes.Tests.Http;

[Trait("Category", "Integration")]
public sealed class ContactHttpTests
{
    [Theory]
    [InlineData("pending")]
    [InlineData("needsAction")]
    [InlineData("rejected")]
    [InlineData("accepted")]
    public async Task Request_PublicCard_ExplicitRecipientDecisionAcrossRestart(string status)
    {
        await using var context = new MailboxHttpContext();
        var card = context.Data.Card(); Require(context.Data.Store.PutCard(Bob, card, Ct)); await context.Start();
        var discovery = await MailboxHttpContext.Json(await context.Send("GET", "/v1/contacts/card?cardId=bob-public", null));
        discovery.GetProperty("cardId").GetString().ShouldBe("bob-public");
        var submission = context.Data.Submission(card);
        var request = await MailboxHttpContext.Json(await context.Send("POST", "/v1/contacts/requests", body: new
        { requestId = submission.RequestId.Value, cardId = card.CardId.Value, methodId = "relay", payload = MailboxHttpContext.Payload(submission.Payload) }));
        request.GetProperty("request").GetProperty("status").GetString().ShouldBe("pending");
        request.GetProperty("request").GetProperty("requesterMailboxId").GetString().ShouldBe("alice");
        var generation = request.GetProperty("generation").GetInt64();
        await context.Restart();
        var path = "/v1/contacts/requests/" + submission.RequestId.Value;
        var polled = await MailboxHttpContext.Json(await context.Send("GET", "/v1/contacts/requests", "bob"));
        polled.GetProperty("items").GetArrayLength().ShouldBe(1);
        polled.GetProperty("items")[0].TryGetProperty("payload", out _).ShouldBeFalse();
        var body = new { requesterMailboxId = "alice", expectedGeneration = generation, status, reply = (object?)null };
        if (status != "pending") await MailboxHttpContext.Json(await context.Send("POST", path + "/decision", "bob", body));
        await context.Restart();
        var summary = await MailboxHttpContext.Json(await context.Send("GET", path + "?requesterMailboxId=alice")); summary.GetProperty("status").GetString().ShouldBe(status);
        (await context.Send("GET", path + "?requesterMailboxId=alice", "eve")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await context.Send("POST", path + "/decision", "eve", body)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await context.Send("POST", path + "/decision", "alice", body)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var inbox = await MailboxHttpContext.Json(await context.Send("GET", "/v1/inbox", "bob")); inbox.GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Cards_PublicPrivateAndLifetime_IndependentListingAndOwnerExport()
    {
        await using var context = new MailboxHttpContext();
        foreach (var card in new[] { context.Data.Card("public"), context.Data.Card("private", ContactVisibility.Unlisted),
            context.Data.Card("expired") with { ExpiresAt = context.Data.Clock.GetUtcNow().AddSeconds(1) },
            context.Data.Card("revoked") with { RevokedAt = context.Data.Clock.GetUtcNow() } })
            Require(context.Data.Store.PutCard(Bob, card, Ct));
        context.Data.Clock.Advance(TimeSpan.FromSeconds(1)); await context.Start();
        var publicPage = await MailboxHttpContext.Json(await context.Send("GET", "/v1/contacts/cards", null));
        publicPage.GetProperty("items").GetArrayLength().ShouldBe(1);
        publicPage.GetProperty("items")[0].GetProperty("cardId").GetString().ShouldBe("public");
        foreach (var owner in new string?[] { null, "alice" })
            (await context.Send("GET", "/v1/contacts/card?cardId=private", owner)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await context.Send("GET", "/v1/contacts/card?cardId=private", "bob")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await context.Send("GET", "/v1/contacts/owned-cards", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var owned = await MailboxHttpContext.Json(await context.Send("GET", "/v1/contacts/owned-cards", "bob"));
        owned.GetProperty("items").GetArrayLength().ShouldBe(2);
        (await context.Send("GET", "/v1/contacts/cards", "wrong-key")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await context.Send("GET", "/v1/contacts/card?cardId=expired", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Card_Publication_DerivesOwnerAndPrivateNeverAutoaccepts()
    {
        await using var context = new MailboxHttpContext(); await context.Start(); var now = context.Data.Clock.GetUtcNow();
        var body = new { cardId = "private-export", visibility = "unlisted", createdAt = now, expiresAt = (DateTimeOffset?)null,
            revokedAt = (DateTimeOffset?)null, audienceHint = "intranet", methods = new[] { new { methodId = "relay", version = 1, transport = "relay", endpoint = "https://relay.example.test", instructions = "opaque" } } };
        var published = await MailboxHttpContext.Json(await context.Send("PUT", "/v1/contacts/cards", "bob", body));
        published.GetProperty("ownerMailboxId").GetString().ShouldBe("bob");
        var submission = context.Data.Submission(context.Data.Card("private-export"));
        var request = await MailboxHttpContext.Json(await context.Send("POST", "/v1/contacts/requests", body: new
        { requestId = submission.RequestId.Value, cardId = "private-export", methodId = "relay", payload = MailboxHttpContext.Payload(submission.Payload) }));
        request.GetProperty("request").GetProperty("status").GetString().ShouldBe("pending");
    }

    [Fact]
    public async Task Blocks_ParticipantFlagsAndGeneration_FenceEveryCard()
    {
        await using var context = new MailboxHttpContext(); var relation = context.Data.Connect(); await context.Start();
        var channel = await MailboxHttpContext.Json(await context.Send("GET", "/v1/contacts/channel?peerMailboxId=alice", "bob"));
        channel.GetProperty("generation").GetInt64().ShouldBe(relation.Generation);
        (await context.Send("GET", "/v1/contacts/channel?peerMailboxId=alice", "eve")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var blocked = await MailboxHttpContext.Json(await context.Send("PUT", "/v1/contacts/blocks", "bob", new { peerMailboxId = "alice", expectedGeneration = relation.Generation }));
        blocked.GetProperty("isBlockedByMailbox").GetBoolean().ShouldBeTrue();
        var current = blocked.GetProperty("generation").GetInt64();
        var ownClear = await MailboxHttpContext.Json(await context.Send("DELETE", "/v1/contacts/blocks", "alice", new { peerMailboxId = "bob", expectedGeneration = current }));
        ownClear.GetProperty("isBlockedByPeer").GetBoolean().ShouldBeTrue();
        (await context.Send("DELETE", "/v1/contacts/blocks", "bob", new { peerMailboxId = "alice", expectedGeneration = relation.Generation })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        Require(context.Data.Store.PutCard(Bob, context.Data.Card("another"), Ct));
        var submission = context.Data.Submission(context.Data.Card("another"));
        (await context.Send("POST", "/v1/contacts/requests", body: new { requestId = submission.RequestId.Value, cardId = "another", methodId = "relay", payload = MailboxHttpContext.Payload(submission.Payload) })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await MailboxHttpContext.Json(await context.Send("DELETE", "/v1/contacts/blocks", "bob", new { peerMailboxId = "alice", expectedGeneration = current }));
        (await context.Send("POST", "/v1/messages", body: context.Message(relation))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Decision_OptionalOpaqueReply_OnlyRequesterInboxContainsPayload()
    {
        await using var context = new MailboxHttpContext(); var request = context.Data.Request(); await context.Start();
        var reply = context.Data.Payload("secret reply");
        await MailboxHttpContext.Json(await context.Send("POST", $"/v1/contacts/requests/{request.Request.RequestId.Value}/decision", "bob",
            new { requesterMailboxId = "alice", expectedGeneration = request.Generation, status = "needsAction", reply = MailboxHttpContext.Payload(reply) }));
        var summary = await MailboxHttpContext.Json(await context.Send("GET", $"/v1/contacts/requests/{request.Request.RequestId.Value}?requesterMailboxId=alice"));
        summary.TryGetProperty("reply", out _).ShouldBeFalse(); summary.GetProperty("replyMessageId").GetGuid().ShouldBe(reply.MessageId);
        var inbox = await MailboxHttpContext.Json(await context.Send("GET", "/v1/inbox")); inbox.GetProperty("items")[0].GetProperty("envelope").GetProperty("payload").GetProperty("messageId").GetGuid().ShouldBe(reply.MessageId);
    }
    private static CancellationToken Ct => MailboxHttpContext.Ct;
    [Fact]
    public async Task Cards_UnicodeOwner_GeneratedCursorRemainsUsable()
    {
        var owner = new string('界', 128);
        await using var context = new MailboxHttpContext(o => o with
        { Credentials = [.. o.Credentials.Select(c => c.MailboxId == "bob" ? c with { MailboxId = owner } : c)] });
        var authority = new MailboxAuthority(new(owner));
        Require(context.Data.Store.PutCard(authority, context.Data.Card("first", owner: authority), Ct));
        Require(context.Data.Store.PutCard(authority, context.Data.Card("second", owner: authority), Ct));
        await context.Start();
        var first = await MailboxHttpContext.Json(await context.Send("GET", "/v1/contacts/owned-cards?limit=1", "bob"));
        var cursor = first.GetProperty("nextCursor").GetString().ShouldNotBeNull();
        var second = await MailboxHttpContext.Json(await context.Send("GET", "/v1/contacts/owned-cards?afterCursor=" + Uri.EscapeDataString(cursor), "bob"));
        second.GetProperty("items").GetArrayLength().ShouldBe(1);
        second.GetProperty("items")[0].GetProperty("cardId").GetString().ShouldBe("second");
    }
    [Fact]
    public async Task Cards_PublicIdNamedOwned_RemainsDiscoverable()
    {
        await using var context = new MailboxHttpContext(); Require(context.Data.Store.PutCard(Bob, context.Data.Card("owned"), Ct)); await context.Start();
        var card = await MailboxHttpContext.Json(await context.Send("GET", "/v1/contacts/card?cardId=owned", null));
        card.GetProperty("cardId").GetString().ShouldBe("owned");
    }
}
