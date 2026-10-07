using Weave.Contacts;
using static Weave.Mailboxes.Tests.Storage.MailboxTestContext;

namespace Weave.Mailboxes.Tests.Storage;

public sealed class MailboxRequestScopeTests
{
    [Fact]
    public void Request_SameIdDifferentRequesters_IsolatesAckReplyBlockPurgeAndRestart()
    {
        using var context = new MailboxTestContext();
        var card = context.Card();
        Require(context.Store.PutCard(Bob, card, Ct));
        var submission = context.Submission(card);
        var alice = Require(context.Store.RequestContact(Alice, submission, Ct));
        var eve = Require(context.Store.RequestContact(Eve, submission, Ct));
        eve.Request.RequestId.ShouldBe(alice.Request.RequestId);
        Require(context.Store.RequestContact(Eve, submission, Ct)).Request.ShouldBe(eve.Request);
        var changed = submission with { Payload = context.Payload("changed") };
        context.Store.RequestContact(Eve, changed, Ct).Error.ShouldBe(MailboxError.Conflict);
        context.Store.GetContactRequest(Eve, alice.Request.Locator, Ct).ShouldBeNull();
        context.Store.GetContactRequest(Alice, eve.Request.Locator, Ct).ShouldBeNull();
        Require(context.Store.Acknowledge(Bob, Alice.MailboxId, submission.Payload.MessageId, Ct));
        context.Store.ReadPending(Bob, null, 10, Ct).Items.Single().SenderMailboxId.ShouldBe(Eve.MailboxId);
        var reply = context.Payload("alice reply");
        Require(context.Store.DecideContact(Bob, new(alice.Request.Locator, alice.Generation, ContactStatus.NeedsAction, reply), Ct));
        context.Store.DecideContact(Bob, new(eve.Request.Locator, eve.Generation, ContactStatus.NeedsAction, reply), Ct).Error.ShouldBe(MailboxError.Conflict);
        context.Store.GetContactRequest(Bob, eve.Request.Locator, Ct)!.Status.ShouldBe(ContactStatus.Pending);
        var eveReply = context.Payload("eve reply");
        Require(context.Store.DecideContact(Bob, new(eve.Request.Locator, eve.Generation, ContactStatus.Accepted, eveReply), Ct));
        context.Store.ReadPending(Alice, null, 10, Ct).Items.Single().Envelope.MessageId.ShouldBe(reply.MessageId);
        context.Store.ReadPending(Eve, null, 10, Ct).Items.Single().Envelope.MessageId.ShouldBe(eveReply.MessageId);
        Require(context.Store.SetBlocked(Bob, Alice.MailboxId, alice.Generation, true, Ct));
        context.Store.GetContactRequest(Bob, eve.Request.Locator, Ct)!.Status.ShouldBe(ContactStatus.Accepted);
        context.Clock.Advance(TimeSpan.FromDays(8));
        while (context.Store.Sweep(1000, Ct) != 0)
        { }
        var restarted = context.Restart();
        restarted.GetContactRequest(Bob, alice.Request.Locator, Ct).ShouldBeNull();
        restarted.GetContactRequest(Bob, eve.Request.Locator, Ct)!.Status.ShouldBe(ContactStatus.Accepted);
        restarted.GetContactChannel(Bob, Eve.MailboxId, Ct)!.CurrentRequest.ShouldBe(eve.Request.Locator);
        Require(restarted.Send(Eve, new(1, Bob.MailboxId, eve.Generation, context.Payload("new live")), Ct));
        restarted.RequestContact(Alice, submission, Ct).Error.ShouldBe(MailboxError.Expired);
        context.Scalar("SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL").ShouldBe(1);
        context.Scalar("SELECT count(*) FROM mailbox_messages WHERE request_requester='alice' AND payload IS NOT NULL").ShouldBe(0);
    }
    [Fact]
    public void ReadPending_MessagePurposeLocatorDiffers_FailsClosedWithoutOtherRequesterBody()
    {
        using var context = new MailboxTestContext();
        var request = context.Request();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = context.Options.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE mailbox_messages SET request_requester='eve' WHERE purpose=1";
        command.ExecuteNonQuery().ShouldBe(1);
        context.Store.ReadPending(Bob, null, 10, Ct).Items.ShouldBeEmpty();
        context.Store.GetContactRequest(Bob, request.Request.Locator, Ct)!.RequesterMailboxId.ShouldBe(Alice.MailboxId);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
