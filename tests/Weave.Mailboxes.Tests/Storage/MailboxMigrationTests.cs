using Weave.Contacts;
using static Weave.Mailboxes.Tests.Storage.MailboxTestContext;

namespace Weave.Mailboxes.Tests.Storage;

public sealed class MailboxMigrationTests
{
    [Fact]
    public void Open_RetainedV1_PreservesEpochPayloadNullRetryAndSequence()
    {
        using var fixture = new MailboxV1Fixture();
        var store = fixture.Open();
        fixture.Scalar("SELECT version FROM mailbox_schema").ShouldBe(2);
        fixture.Scalar("SELECT seq FROM sqlite_sequence WHERE name='contact_requests'").ShouldBe(12);
        Require(store.RequestContact(Alice, fixture.Request, Ct)).Request.Status.ShouldBe(ContactStatus.Accepted);
        Require(store.DecideContact(Bob, new(new(Alice.MailboxId, fixture.Request.RequestId), 1, ContactStatus.Accepted, fixture.Reply), Ct)).Request.ReplyMessageId.ShouldBe(fixture.Reply.MessageId);
        Require(store.Send(Alice, new(1, Bob.MailboxId, 1, fixture.Live), Ct)).State.ShouldBe(MailboxReceiptState.Pending);
        fixture.Scalar("SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL").ShouldBe(1);
        store.GetContactChannel(Bob, Eve.MailboxId, Ct)!.Generation.ShouldBe(3);
        store.GetContactChannel(Bob, Eve.MailboxId, Ct)!.IsBlockedByPeer.ShouldBeTrue();
        var ack = Require(store.Acknowledge(Bob, Alice.MailboxId, fixture.Live.MessageId, Ct));
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var reopened = fixture.Open();
        reopened.GetReceipt(Alice, fixture.Live.MessageId, Ct).ShouldBe(ack);
        Require(reopened.Acknowledge(Bob, Alice.MailboxId, fixture.Live.MessageId, Ct)).ShouldBe(ack);
        fixture.Scalar("SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL").ShouldBe(0);
        Require(reopened.RequestContact(Alice, fixture.Request, Ct)).Request.Status.ShouldBe(ContactStatus.Accepted);
        fixture.Scalar("SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL").ShouldBe(0);
    }
    [Fact]
    public void Open_MigrationTransactionFails_RollsBackWithoutResetOrResurrection()
    {
        using var fixture = new MailboxV1Fixture(); fixture.SetMigrationFailure(true);
        Should.Throw<Microsoft.Data.Sqlite.SqliteException>(() => fixture.Open());
        fixture.Scalar("SELECT version FROM mailbox_schema").ShouldBe(1);
        fixture.Scalar("SELECT count(*) FROM pragma_table_info('contact_channels') WHERE name='current_requester'").ShouldBe(0);
        fixture.Scalar("SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL").ShouldBe(1);
        fixture.Scalar("SELECT seq FROM sqlite_sequence WHERE name='contact_requests'").ShouldBe(12);
        fixture.SetMigrationFailure(false);
        var reopened = fixture.Open();
        fixture.Scalar("SELECT version FROM mailbox_schema").ShouldBe(2);
        Require(reopened.RequestContact(Alice, fixture.Request, Ct)).Request.Status.ShouldBe(ContactStatus.Accepted);
        fixture.Scalar("SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL").ShouldBe(1);
    }

    [Fact]
    public async Task Open_ConcurrentRetainedV1_MigratesOnceWithBothOwnersPreserved()
    {
        using var fixture = new MailboxV1Fixture(); using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() => { start.Wait(Ct); return fixture.Open(); }, Ct)).ToArray();
        start.Set(); var stores = await Task.WhenAll(tasks);
        fixture.Scalar("SELECT version FROM mailbox_schema").ShouldBe(2);
        fixture.Scalar("SELECT count(*) FROM contact_requests").ShouldBe(1);
        foreach (var store in stores)
        {
            Require(store.RequestContact(Alice, fixture.Request, Ct)).Request.Status.ShouldBe(ContactStatus.Accepted);
            store.GetContactChannel(Bob, Eve.MailboxId, Ct)!.IsBlockedByPeer.ShouldBeTrue();
        }
    }

    [Fact]
    public void Open_MigratedState_ScopedCollisionAndOldCursorPreserveOrdering()
    {
        using var fixture = new MailboxV1Fixture(); var store = fixture.Open();
        store.ReadPending(Bob, "inbox:Ym9i:2", 10, Ct).Items.Single().Envelope.MessageId.ShouldBe(fixture.Live.MessageId);
        Require(store.SetBlocked(Eve, Bob.MailboxId, 3, false, Ct));
        var independentlyScoped = Require(store.RequestContact(Eve, fixture.Request, Ct));
        independentlyScoped.Request.RequesterMailboxId.ShouldBe(Eve.MailboxId);
        fixture.Scalar("SELECT seq FROM contact_requests WHERE requester='eve'").ShouldBe(13);
        var first = store.ListContactRequests(Bob, null, 1, Ct);
        first.Items.Single().RequesterMailboxId.ShouldBe(Alice.MailboxId);
        first.NextCursor.ShouldNotBeNull();
        store.ListContactRequests(Bob, first.NextCursor, 1, Ct).Items.Single().RequesterMailboxId.ShouldBe(Eve.MailboxId);
        var reopened = fixture.Open();
        reopened.GetContactChannel(Bob, Alice.MailboxId, Ct)!.CurrentRequest.ShouldBe(new(Alice.MailboxId, fixture.Request.RequestId));
        reopened.GetContactChannel(Bob, Eve.MailboxId, Ct)!.CurrentRequest.ShouldBe(independentlyScoped.Request.Locator);
        Require(reopened.RequestContact(Alice, fixture.Request, Ct)).Request.Status.ShouldBe(ContactStatus.Accepted);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
