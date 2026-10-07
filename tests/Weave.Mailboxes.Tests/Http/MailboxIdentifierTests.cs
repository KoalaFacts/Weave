using System.Net;
using Weave.Contacts;
using static Weave.Mailboxes.Tests.Storage.MailboxTestContext;

namespace Weave.Mailboxes.Tests.Http;

[Trait("Category", "Integration")]
public sealed class MailboxIdentifierTests
{
    [Theory]
    [InlineData("ali/ce")]
    [InlineData("ali%2Fce")]
    [InlineData("../alice")]
    [InlineData("界/点%2F")]
    [InlineData("owned")]
    public async Task Identifiers_QueryAndJsonRoundTrip_ExactAckAndBlockTargets(string peer)
    {
        await using var context = new MailboxHttpContext(o => o with { Credentials = [.. o.Credentials.Select(c => c.MailboxId == "alice" ? c with { MailboxId = peer } : c)] });
        var owner = new MailboxAuthority(new(peer));
        var request = context.Data.Request(requester: owner);
        var connected = Require(context.Data.Store.DecideContact(Bob, new(request.Request.Locator, request.Generation, ContactStatus.Accepted), Ct));
        await context.Start();
        var channel = await MailboxHttpContext.Json(await context.Send("GET", "/v1/contacts/channel?peerMailboxId=" + Uri.EscapeDataString(peer), "bob"));
        channel.GetProperty("peerMailboxId").GetString().ShouldBe(peer);
        var ack = await MailboxHttpContext.Json(await context.Send("POST", $"/v1/inbox/{request.Request.RequestMessageId}/ack", "bob", new { senderMailboxId = peer }));
        ack.GetProperty("senderMailboxId").GetString().ShouldBe(peer);
        ack.GetProperty("state").GetString().ShouldBe("acknowledged");
        var blocked = await MailboxHttpContext.Json(await context.Send("PUT", "/v1/contacts/blocks", "bob", new { peerMailboxId = peer, expectedGeneration = connected.Generation }));
        blocked.GetProperty("peerMailboxId").GetString().ShouldBe(peer);
        context.Data.Store.GetContactChannel(Bob, owner.MailboxId, Ct)!.IsBlockedByMailbox.ShouldBeTrue();
    }

    [Theory]
    [InlineData("card/slash")]
    [InlineData("card%2Fslash")]
    [InlineData("../owned")]
    [InlineData("卡/片%点")]
    [InlineData("owned")]
    public async Task Cards_BroadIdentifiers_SingleParsePublicationAndDiscovery(string id)
    {
        await using var context = new MailboxHttpContext(); await context.Start(); var now = context.Data.Clock.GetUtcNow();
        var published = await MailboxHttpContext.Json(await context.Send("PUT", "/v1/contacts/cards", "bob", new
        { cardId = id, visibility = "public", createdAt = now, expiresAt = (DateTimeOffset?)null, revokedAt = (DateTimeOffset?)null,
            audienceHint = (string?)null, methods = new[] { new { methodId = "relay", version = 1, transport = "relay", endpoint = "https://relay.example.test", instructions = "opaque" } } }));
        published.GetProperty("cardId").GetString().ShouldBe(id);
        var found = await MailboxHttpContext.Json(await context.Send("GET", "/v1/contacts/card?cardId=" + Uri.EscapeDataString(id), null));
        found.GetProperty("cardId").GetString().ShouldBe(id);
        (await context.Send("GET", "/v1/contacts/card?cardId=" + Uri.EscapeDataString(id) + "&cardId=other", null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Request_ForeignIdWithInvalidOrValidCard_NoCollisionDisclosure()
    {
        await using var context = new MailboxHttpContext(); var alice = context.Data.Request(); await context.Start();
        var foreign = alice.Request.RequestId.Value;
        var fresh = context.Data.Submission().RequestId.Value;
        foreach (var id in new[] { foreign, fresh })
            (await context.Send("POST", "/v1/contacts/requests", "eve", new { requestId = id, cardId = "missing", methodId = "relay", payload = MailboxHttpContext.Payload(context.Data.Payload()) })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var evePayload = context.Data.Payload();
        var admitted = await MailboxHttpContext.Json(await context.Send("POST", "/v1/contacts/requests", "eve", new
        { requestId = foreign, cardId = "bob-public", methodId = "relay", payload = MailboxHttpContext.Payload(evePayload) }));
        admitted.GetProperty("request").GetProperty("requesterMailboxId").GetString().ShouldBe("eve");
        context.Data.Store.ListContactRequests(Bob, null, 10, Ct).Items.Length.ShouldBe(2);
        var changed = await context.Send("POST", "/v1/contacts/requests", "eve", new
        { requestId = foreign, cardId = "bob-public", methodId = "relay", payload = MailboxHttpContext.Payload(context.Data.Payload()) });
        changed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }
    [Fact]
    public async Task Aliases_SlashAndLiteralPercent_KeepAckAndBlockSeparate()
    {
        await using var context = new MailboxHttpContext(o => o with { Credentials = [.. o.Credentials.Select(c => c with
            { MailboxId = c.MailboxId == "alice" ? "ali/ce" : c.MailboxId == "eve" ? "ali%2Fce" : c.MailboxId })] });
        var slash = new MailboxAuthority(new("ali/ce")); var percent = new MailboxAuthority(new("ali%2Fce"));
        var first = context.Data.Request(requester: slash); var second = context.Data.Request(requester: percent);
        Require(context.Data.Store.DecideContact(Bob, new(first.Request.Locator, first.Generation, ContactStatus.Accepted), Ct));
        Require(context.Data.Store.DecideContact(Bob, new(second.Request.Locator, second.Generation, ContactStatus.Accepted), Ct));
        await context.Start();
        (await context.Send("POST", $"/v1/inbox/{first.Request.RequestMessageId}/ack", "bob", new { senderMailboxId = "ali%2Fce" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await MailboxHttpContext.Json(await context.Send("POST", $"/v1/inbox/{first.Request.RequestMessageId}/ack", "bob", new { senderMailboxId = "ali/ce" }));
        await MailboxHttpContext.Json(await context.Send("PUT", "/v1/contacts/blocks", "bob", new { peerMailboxId = "ali/ce", expectedGeneration = first.Generation }));
        context.Data.Store.GetContactChannel(Bob, slash.MailboxId, Ct)!.IsBlockedByMailbox.ShouldBeTrue();
        context.Data.Store.GetContactChannel(Bob, percent.MailboxId, Ct)!.IsBlocked.ShouldBeFalse();
        var intact = await MailboxHttpContext.Json(await context.Send("GET", "/v1/contacts/channel?peerMailboxId=ali%252Fce", "bob"));
        intact.GetProperty("peerMailboxId").GetString().ShouldBe("ali%2Fce");
        (await context.Send("GET", "/v1/contacts/channel?peerMailboxId=ali%2Fce&peerMailboxId=ali%252Fce", "bob")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var inbox = await MailboxHttpContext.Json(await context.Send("GET", "/v1/inbox", "bob"));
        inbox.GetProperty("items").GetArrayLength().ShouldBe(1);
        inbox.GetProperty("items")[0].GetProperty("senderMailboxId").GetString().ShouldBe("ali%2Fce");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Request_ExistingOrFreshForeignIdWithValidCard_SameIndependentPendingOutcome(bool existing)
    {
        await using var context = new MailboxHttpContext(); var alice = context.Data.Request(); await context.Start();
        var id = existing ? alice.Request.RequestId.Value : context.Data.Submission().RequestId.Value;
        var payload = context.Data.Payload();
        var body = new { requestId = id, cardId = "bob-public", methodId = "relay", payload = MailboxHttpContext.Payload(payload) };
        var admitted = await MailboxHttpContext.Json(await context.Send("POST", "/v1/contacts/requests", "eve", body));
        admitted.GetProperty("request").GetProperty("status").GetString().ShouldBe("pending");
        admitted.GetProperty("request").GetProperty("requesterMailboxId").GetString().ShouldBe("eve");
        await MailboxHttpContext.Json(await context.Send("POST", "/v1/contacts/requests", "eve", body));
        var path = "/v1/contacts/requests/" + id;
        (await context.Send("GET", path + "?requesterMailboxId=eve", "alice")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await context.Send("GET", path + "?requesterMailboxId=eve&requesterMailboxId=alice", "bob")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var eve = await MailboxHttpContext.Json(await context.Send("GET", path + "?requesterMailboxId=eve", "bob"));
        eve.GetProperty("requesterMailboxId").GetString().ShouldBe("eve");
        await MailboxHttpContext.Json(await context.Send("POST", path + "/decision", "bob", new
            { requesterMailboxId = "eve", expectedGeneration = eve.GetProperty("generation").GetInt64(), status = "accepted", reply = (object?)null }));
        var untouched = await MailboxHttpContext.Json(await context.Send("GET", "/v1/contacts/requests/" + alice.Request.RequestId.Value + "?requesterMailboxId=alice", "bob"));
        untouched.GetProperty("status").GetString().ShouldBe("pending");
    }

    [Theory]
    [InlineData("ack")]
    [InlineData("decision")]
    [InlineData("block")]
    public async Task Identity_BodyBeyondDomainBound_Invalid(string operation)
    {
        await using var context = new MailboxHttpContext(); var request = context.Data.Request(); await context.Start();
        var identity = new string('x', 129);
        using var response = operation switch
        {
            "ack" => await context.Send("POST", $"/v1/inbox/{request.Request.RequestMessageId}/ack", "bob", new { senderMailboxId = identity }),
            "decision" => await context.Send("POST", $"/v1/contacts/requests/{request.Request.RequestId.Value}/decision", "bob", new
                { requesterMailboxId = identity, expectedGeneration = request.Generation, status = "accepted", reply = (object?)null }),
            "block" => await context.Send("PUT", "/v1/contacts/blocks", "bob", new { peerMailboxId = identity, expectedGeneration = request.Generation }),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        context.Data.Store.GetContactRequest(Bob, request.Request.Locator, Ct)!.Status.ShouldBe(ContactStatus.Pending);
    }

    private static CancellationToken Ct => MailboxHttpContext.Ct;
}
