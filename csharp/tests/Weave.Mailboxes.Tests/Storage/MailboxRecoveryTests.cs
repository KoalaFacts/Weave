using Weave.Contacts;
using Weave.Mailboxes.Sqlite;
using static Weave.Mailboxes.Tests.Storage.MailboxTestContext;

namespace Weave.Mailboxes.Tests.Storage;

public sealed class MailboxRecoveryTests
{
    [Fact]
    public void RequestContact_LostResponse_RetryAfterRestartPreservesGenerationAndPayload()
    {
        using var context = new MailboxTestContext();
        Require(context.Store.PutCard(Bob, context.Card(), TestContext.Current.CancellationToken));
        var request = context.Submission();
        var admitted = Require(context.Store.RequestContact(Alice, request, TestContext.Current.CancellationToken));
        var recovered = Require(context.Restart().RequestContact(Alice, request, TestContext.Current.CancellationToken));
        recovered.ShouldBe(admitted);
        context.Store.ReadPending(Bob, null, 10, TestContext.Current.CancellationToken).Items.Length.ShouldBe(1);
    }

    [Fact]
    public void DecideContact_NeedsAction_RestartRetainsOnlyMetadataAndExpiringInbox()
    {
        using var context = new MailboxTestContext();
        var request = context.Request();
        var reply = context.Payload("answer", TimeSpan.FromSeconds(5));
        Require(context.Store.DecideContact(Bob,
            new(request.Request.Locator, request.Generation, ContactStatus.NeedsAction, reply), TestContext.Current.CancellationToken));
        var restarted = context.Restart();
        restarted.GetContactRequest(Alice, request.Request.Locator, TestContext.Current.CancellationToken)!.ReplyMessageId.ShouldBe(reply.MessageId);
        restarted.ReadPending(Alice, null, 10, TestContext.Current.CancellationToken).Items.Single().Envelope.Payload.Bytes.ShouldBe(reply.Bytes);
        context.Clock.Advance(TimeSpan.FromSeconds(5));
        restarted.ReadPending(Alice, null, 10, TestContext.Current.CancellationToken).Items.ShouldBeEmpty();
        restarted.Sweep(100, TestContext.Current.CancellationToken).ShouldBeGreaterThan(0);
        context.Scalar("SELECT count(*) FROM mailbox_messages WHERE sender = 'bob' AND payload IS NOT NULL").ShouldBe(0);
    }

    [Fact]
    public void Restart_IsolatedEmptyDatabase_DoesNotImportPayloadsOrGrants()
    {
        using var context = new MailboxTestContext();
        var relation = context.Connect();
        var envelope = context.Envelope(relation);
        Require(context.Store.Send(Alice, envelope, TestContext.Current.CancellationToken));
        var options = context.Options with { DatabasePath = context.Options.DatabasePath + ".empty" };
        var empty = new SqliteMailboxStore(options, context.Clock);
        empty.ReadPending(Bob, null, 10, TestContext.Current.CancellationToken).Items.ShouldBeEmpty();
        empty.GetReceipt(Alice, envelope.MessageId, TestContext.Current.CancellationToken).ShouldBeNull();
        empty.Send(Alice, envelope, TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Forbidden);
        empty.GetContactChannel(Alice, Bob.MailboxId, TestContext.Current.CancellationToken).ShouldBeNull();
    }

    [Fact]
    public void Restart_MissingRequiredDatabase_FailsClosed()
    {
        using var context = new MailboxTestContext();
        Should.Throw<InvalidOperationException>(() => new SqliteMailboxStore(context.Options with
        { DatabasePath = context.Options.DatabasePath + ".missing", RequireExistingStorage = true }, context.Clock));
    }

    [Fact]
    public void Send_CancelledCall_DoesNotAdmitPayload()
    {
        using var context = new MailboxTestContext();
        var relation = context.Connect();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var envelope = context.Envelope(relation);
        Should.Throw<OperationCanceledException>(() => context.Store.Send(Alice, envelope, cancellation.Token));
        context.Store.ReadPending(Bob, null, 10, TestContext.Current.CancellationToken).Items.ShouldBeEmpty();
    }
}
