using System.Net;
using System.Net.Sockets;
using System.Text;
using Weave.Mailbox.Host;
using static Weave.Mailboxes.Tests.Storage.MailboxTestContext;

namespace Weave.Mailboxes.Tests.Http;

[Trait("Category", "Integration")]
public sealed class MailboxHttpBoundaryTests
{
    [Theory]
    [InlineData("{\"version\":1,\"recipientMailboxId\":\"bob\",\"contactGeneration\":1,\"payload\":null}")]
    [InlineData("{\"version\":1}")]
    [InlineData("null")]
    [InlineData("{")]
    public async Task Send_MalformedOrMissingRequiredFields_Invalid(string json)
    {
        await using var context = new MailboxHttpContext(); await context.Start();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add(MailboxControlAuthentication.HeaderName, context.Secret("alice"));
        using var response = await context.Client.SendAsync(request, MailboxHttpContext.Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(MailboxHttpContext.Ct)).ShouldNotContain("Exception");
    }

    [Fact]
    public async Task Discovery_DuplicateControlHeader_FailsClosed()
    {
        await using var context = new MailboxHttpContext(); Require(context.Data.Store.PutCard(Bob, context.Data.Card(), MailboxHttpContext.Ct)); await context.Start();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/contacts/cards");
        request.Headers.Add(MailboxControlAuthentication.HeaderName, new[] { context.Secret("alice"), "wrong" });
        using var response = await context.Client.SendAsync(request, MailboxHttpContext.Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Send_OpaquePayloadUrl_NeverConnectsToPayloadAddress()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        await using var context = new MailboxHttpContext(); var relation = context.Data.Connect(); await context.Start();
        var address = (IPEndPoint)listener.LocalEndpoint;
        var text = $"https://127.0.0.1:{address.Port}/private-payload";
        await MailboxHttpContext.Json(await context.Send("POST", "/v1/messages", body: context.Message(relation, context.Data.Payload(text))));
        var inbox = await MailboxHttpContext.Json(await context.Send("GET", "/v1/inbox", "bob"));
        Encoding.UTF8.GetString(inbox.GetProperty("items")[0].GetProperty("envelope").GetProperty("payload").GetProperty("bytes").GetBytesFromBase64()).ShouldBe(text);
        listener.Pending().ShouldBeFalse();
    }

    [Fact]
    public async Task Cleanup_ExpiredPayload_InternalServiceWithoutMaintenanceEndpoint()
    {
        await using var context = new MailboxHttpContext(o => o with { CleanupInterval = TimeSpan.FromSeconds(1), CleanupBatchSize = 1 });
        var relation = context.Data.Connect(); var payload = context.Data.Payload(lifetime: TimeSpan.FromSeconds(1));
        Require(context.Data.Store.Send(Alice, context.Data.Envelope(relation, payload), MailboxHttpContext.Ct));
        await context.Start(); context.Data.Clock.Advance(TimeSpan.FromSeconds(1));
        (await context.Send("POST", "/v1/maintenance/sweep")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(MailboxHttpContext.Ct); deadline.CancelAfter(TimeSpan.FromSeconds(8));
        while (context.Data.Scalar("SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL") != 0)
            await Task.Delay(50, deadline.Token);
        context.Data.Store.GetReceipt(Alice, payload.MessageId, MailboxHttpContext.Ct)!.State.ShouldBe(MailboxReceiptState.Expired);
    }

    [Fact]
    public async Task Send_ExpiredAndConflictingInputs_DistinctBoundedErrors()
    {
        await using var context = new MailboxHttpContext(); var relation = context.Data.Connect(); await context.Start();
        var payload = context.Data.Payload(lifetime: TimeSpan.FromSeconds(1)); context.Data.Clock.Advance(TimeSpan.FromSeconds(1));
        var expired = await MailboxHttpContext.Json(await context.Send("POST", "/v1/messages", body: context.Message(relation, payload)), HttpStatusCode.Gone);
        expired.GetProperty("code").GetString().ShouldBe("expired");
        var live = context.Data.Payload(); await MailboxHttpContext.Json(await context.Send("POST", "/v1/messages", body: context.Message(relation, live)));
        var modified = new MailboxPayload(live.MessageId, live.CreatedAt, live.ExpiresAt, live.PayloadEncoding, "other"u8);
        var conflict = await MailboxHttpContext.Json(await context.Send("POST", "/v1/messages", body: context.Message(relation, modified)), HttpStatusCode.Conflict);
        conflict.GetProperty("code").GetString().ShouldBe("conflict");
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("hash")]
    [InlineData("bounds")]
    public void Create_InvalidControlOrBounds_FailsClosed(string invalid)
    {
        using var data = new Weave.Mailboxes.Tests.Storage.MailboxTestContext();
        var options = new MailboxHostOptions { Credentials = [new("alice", new string('A', 64))] };
        options = invalid switch
        {
            "empty" => options with { Credentials = [] }, "hash" => options with { Credentials = [new("alice", "invalid")] },
            "bounds" => options with { PollInterval = TimeSpan.FromMilliseconds(1) }, _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        };
        Should.Throw<ArgumentException>(() => MailboxHost.Create(options, data.Store, data.Clock, new()));
    }
    [Fact]
    public async Task Inbox_StorageMissing_Bounded503AndDiagnosticWithoutDetails()
    {
        await using var context = new MailboxHttpContext(); await context.Start();
        File.Move(context.Data.Options.DatabasePath, context.Data.Options.DatabasePath + ".retained");
        var error = await MailboxHttpContext.Json(await context.Send("GET", "/v1/inbox", "bob"), HttpStatusCode.ServiceUnavailable);
        error.GetProperty("code").GetString().ShouldBe("storage");
        error.GetRawText().ShouldNotContain(context.Data.Options.DatabasePath);
        lock (context.Logs)
        {
            context.Logs.ShouldContain("Mailbox request could not access storage.");
            string.Join('\n', context.Logs).ShouldNotContain(context.Data.Options.DatabasePath);
        }
    }
    [Theory]
    [InlineData("/v1/contacts/owned-cards/")]
    [InlineData("/V1/CONTACTS/OWNED-CARDS/")]
    public async Task Cards_OwnerListingTrailingSlash_StillRequiresControl(string path)
    {
        await using var context = new MailboxHttpContext(); await context.Start();
        (await context.Send("GET", path, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
