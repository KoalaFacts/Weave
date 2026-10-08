using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Weave.Contacts;
using Weave.Mailbox.Host;
using static Weave.Mailboxes.Tests.Storage.MailboxTestContext;

namespace Weave.Mailboxes.Tests.Http;

[Trait("Category", "Integration")]
public sealed class MailboxAuthenticationBoundaryTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("whitespaceOnly")]
    [InlineData("embeddedWhitespace")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("unknown")]
    [InlineData("revoked")]
    [InlineData("validThenInvalid")]
    [InlineData("invalidThenValid")]
    [InlineData("repeatedValid")]
    [InlineData("differentValid")]
    [InlineData("commaJoined")]
    public async Task Control_RawMissingOrInvalidHeaders_OnlyMissingAllowsPublicDiscovery(string shape)
    {
        await using var context = new MailboxHttpContext();
        Require(context.Data.Store.PutCard(Bob, context.Data.Card(), Ct));
        await context.Start();
        context.Authentication.Revoke(context.Options.Credentials.Single(c => c.MailboxId == "eve").Sha256);
        var headers = Headers(context, shape);

        var publicList = await RawSend(context, "GET", "/v1/contacts/cards", headers);
        var publicCard = await RawSend(context, "GET", "/v1/contacts/card?cardId=bob-public", headers);
        if (shape == "missing")
        {
            publicList.Status.ShouldBe(HttpStatusCode.OK);
            publicList.Body.GetProperty("items").GetArrayLength().ShouldBe(1);
            publicList.Body.GetProperty("items")[0].GetProperty("cardId").GetString().ShouldBe("bob-public");
            publicCard.Status.ShouldBe(HttpStatusCode.OK);
            publicCard.Body.GetProperty("ownerMailboxId").GetString().ShouldBe("bob");
        }
        else
        {
            AssertUnauthenticated(publicList);
            AssertUnauthenticated(publicCard);
        }
        foreach (var path in new[] { "/v1/inbox", "/v1/outbox", "/v1/contacts/owned-cards", "/v1/contacts/requests", "/v1/inbox/events" })
            AssertUnauthenticated(await RawSend(context, "GET", path, headers));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("whitespaceOnly")]
    [InlineData("embeddedWhitespace")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("unknown")]
    [InlineData("revoked")]
    [InlineData("validThenInvalid")]
    [InlineData("invalidThenValid")]
    [InlineData("repeatedValid")]
    [InlineData("differentValid")]
    [InlineData("commaJoined")]
    public async Task Control_RawMissingOrInvalidHeaders_ProtectedWritesLeaveStorageUnchanged(string shape)
    {
        await using var context = new MailboxHttpContext();
        var relation = context.Data.Connect();
        var payload = context.Data.Payload("denied-send-marker");
        var message = context.Message(relation, payload);
        var now = context.Data.Clock.GetUtcNow();
        var card = new
        {
            cardId = "denied-publication",
            visibility = "public",
            createdAt = now,
            expiresAt = (DateTimeOffset?)null,
            revokedAt = (DateTimeOffset?)null,
            audienceHint = (string?)null,
            methods = new[] { new { methodId = "relay", version = 1, transport = "relay", endpoint = "https://relay.example.test", instructions = "opaque" } }
        };
        await context.Start();
        context.Authentication.Revoke(context.Options.Credentials.Single(c => c.MailboxId == "eve").Sha256);
        var headers = Headers(context, shape);
        var messagesBefore = context.Data.Scalar("SELECT count(*) FROM mailbox_messages");
        var cardsBefore = context.Data.Scalar("SELECT count(*) FROM contact_cards");

        AssertUnauthenticated(await RawSend(context, "POST", "/v1/messages", headers, message));
        AssertUnauthenticated(await RawSend(context, "PUT", "/v1/contacts/cards", headers, card));

        context.Data.Scalar("SELECT count(*) FROM mailbox_messages").ShouldBe(messagesBefore);
        context.Data.Scalar("SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL").ShouldBe(0);
        context.Data.Scalar("SELECT count(*) FROM contact_cards").ShouldBe(cardsBefore);
        context.Data.Store.GetReceipt(Alice, payload.MessageId, Ct).ShouldBeNull();
        context.Data.Store.ReadPending(Bob, null, 10, Ct).Items.ShouldBeEmpty();
        var admitted = await RawSend(context, "POST", "/v1/messages", [context.Secret("alice")], message);
        admitted.Status.ShouldBe(HttpStatusCode.OK);
        admitted.Body.GetProperty("senderMailboxId").GetString().ShouldBe("alice");
        admitted.Body.GetProperty("messageId").GetGuid().ShouldBe(payload.MessageId);
        context.Data.Scalar("SELECT count(*) FROM mailbox_messages").ShouldBe(messagesBefore + 1);
        var published = await RawSend(context, "PUT", "/v1/contacts/cards", [context.Secret("alice")], card);
        published.Status.ShouldBe(HttpStatusCode.OK);
        published.Body.GetProperty("ownerMailboxId").GetString().ShouldBe("alice");
        published.Body.GetProperty("cardId").GetString().ShouldBe("denied-publication");
        context.Data.Scalar("SELECT count(*) FROM contact_cards").ShouldBe(cardsBefore + 1);
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("bob")]
    public async Task Control_RawValidHeader_UsesConfiguredMailboxScope(string owner)
    {
        await using var context = new MailboxHttpContext();
        var relation = context.Data.Connect();
        var payload = context.Data.Payload("bob-only-inbox-marker");
        Require(context.Data.Store.Send(Alice, context.Data.Envelope(relation, payload), Ct));
        Require(context.Data.Store.PutCard(Bob, context.Data.Card("bob-unlisted", ContactVisibility.Unlisted), Ct));
        Require(context.Data.Store.PutCard(Alice, context.Data.Card("alice-public", owner: Alice), Ct));
        await context.Start();
        var headers = new[] { context.Secret(owner) };

        var publicList = await RawSend(context, "GET", "/v1/contacts/cards", headers);
        var owned = await RawSend(context, "GET", "/v1/contacts/owned-cards", headers);
        var inbox = await RawSend(context, "GET", "/v1/inbox", headers);
        var privateCard = await RawSend(context, "GET", "/v1/contacts/card?cardId=bob-unlisted", headers);

        publicList.Status.ShouldBe(HttpStatusCode.OK);
        publicList.Body.GetProperty("items").GetArrayLength().ShouldBe(2);
        owned.Status.ShouldBe(HttpStatusCode.OK);
        foreach (var card in owned.Body.GetProperty("items").EnumerateArray())
            card.GetProperty("ownerMailboxId").GetString().ShouldBe(owner);
        owned.Body.GetProperty("items").GetArrayLength().ShouldBe(owner == "bob" ? 2 : 1);
        inbox.Status.ShouldBe(HttpStatusCode.OK);
        inbox.Body.GetProperty("items").GetArrayLength().ShouldBe(owner == "bob" ? 1 : 0);
        privateCard.Status.ShouldBe(owner == "bob" ? HttpStatusCode.OK : HttpStatusCode.NotFound);
        if (owner == "bob")
        {
            inbox.Body.GetProperty("items")[0].GetProperty("envelope").GetProperty("payload").GetProperty("messageId").GetGuid().ShouldBe(payload.MessageId);
            privateCard.Body.GetProperty("ownerMailboxId").GetString().ShouldBe("bob");
        }
    }

    private static string[] Headers(MailboxHttpContext context, string shape) => shape switch
    {
        "missing" => [],
        "empty" => [string.Empty],
        "whitespaceOnly" => [" \t "],
        "embeddedWhitespace" => [context.Secret("alice").Insert(16, " ")],
        "short" => [new string('A', 31)],
        "long" => [new string('A', 513)],
        "unknown" => [new string('A', 64)],
        "revoked" => [context.Secret("eve")],
        "validThenInvalid" => [context.Secret("alice"), "wrong"],
        "invalidThenValid" => ["wrong", context.Secret("alice")],
        "repeatedValid" => [context.Secret("alice"), context.Secret("alice")],
        "differentValid" => [context.Secret("alice"), context.Secret("bob")],
        "commaJoined" => [context.Secret("alice") + ", " + context.Secret("bob")],
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };

    private static void AssertUnauthenticated((HttpStatusCode Status, JsonElement Body) response)
    {
        response.Status.ShouldBe(HttpStatusCode.Unauthorized);
        response.Body.GetProperty("status").GetInt32().ShouldBe(401);
        response.Body.GetProperty("code").GetString().ShouldBe("unauthenticated");
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> RawSend(MailboxHttpContext context,
        string method, string path, string[] headers, object? body = null)
    {
        var address = context.Client.BaseAddress.ShouldNotBeNull();
        var content = body is null ? string.Empty : JsonSerializer.Serialize(body);
        // HTTP/1.0 plus connection-close keeps responses unchunked while preserving literal repeated header fields.
        var request = new StringBuilder($"{method} {path} HTTP/1.0\r\nHost: {address.Authority}\r\nConnection: close\r\n");
        foreach (var value in headers)
            request.Append(MailboxControlAuthentication.HeaderName).Append(": ").Append(value).Append("\r\n");
        if (body is not null)
            request.Append("Content-Type: application/json\r\n");
        request.Append("Content-Length: ").Append(Encoding.UTF8.GetByteCount(content).ToString(CultureInfo.InvariantCulture))
            .Append("\r\n\r\n").Append(content);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var client = new TcpClient();
        await client.ConnectAsync(address.Host, address.Port, deadline.Token);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request.ToString()), deadline.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var response = await reader.ReadToEndAsync(deadline.Token);
        var separator = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        separator.ShouldBeGreaterThan(0);
        var responseHeaders = response[..separator];
        responseHeaders.ShouldNotContain("Transfer-Encoding:", Case.Insensitive);
        var status = (HttpStatusCode)int.Parse(responseHeaders.Split(' ')[1], CultureInfo.InvariantCulture);
        using var document = JsonDocument.Parse(response[(separator + 4)..]);
        return (status, document.RootElement.Clone());
    }

    private static CancellationToken Ct => MailboxHttpContext.Ct;
}
