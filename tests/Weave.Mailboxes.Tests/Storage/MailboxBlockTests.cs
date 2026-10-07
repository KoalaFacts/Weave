using Weave.Contacts;
using static Weave.Mailboxes.Tests.Storage.MailboxTestContext;

namespace Weave.Mailboxes.Tests.Storage;

public sealed class MailboxBlockTests
{
    [Fact]
    public void SetBlocked_StablePair_StopsNewCardsAndDeletesHandshakeBodies()
    {
        using var context = new MailboxTestContext();
        var request = context.Request();
        Require(context.Store.DecideContact(Bob,
            new(request.Request.Locator, request.Generation, ContactStatus.NeedsAction, context.Payload("challenge")), TestContext.Current.CancellationToken));
        var block = Require(context.Store.SetBlocked(Bob, Alice.MailboxId, request.Generation, true, TestContext.Current.CancellationToken));
        block.IsBlockedByMailbox.ShouldBeTrue();
        block.Generation.ShouldBe(request.Generation + 1);
        context.Store.ReadPending(Alice, null, 10, TestContext.Current.CancellationToken).Items.ShouldBeEmpty();
        context.Store.ReadPending(Bob, null, 10, TestContext.Current.CancellationToken).Items.ShouldBeEmpty();
        context.Scalar("SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL").ShouldBe(0);
        var alternate = context.Card("alternate");
        Require(context.Store.PutCard(Bob, alternate, TestContext.Current.CancellationToken));
        context.Store.RequestContact(Alice, context.Submission(alternate), TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Forbidden);
        Require(context.Store.SetBlocked(Alice, Bob.MailboxId, block.Generation, false, TestContext.Current.CancellationToken)).IsBlockedByPeer.ShouldBeTrue();
        context.Store.RequestContact(Alice, context.Submission(alternate), TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Forbidden);
        var unblocked = Require(context.Store.SetBlocked(Bob, Alice.MailboxId, block.Generation, false, TestContext.Current.CancellationToken));
        context.Store.DecideContact(Bob, new(request.Request.Locator, unblocked.Generation, ContactStatus.Accepted), TestContext.Current.CancellationToken)
            .Error.ShouldBe(MailboxError.Conflict);
        var fresh = Require(context.Store.RequestContact(Alice, context.Submission(alternate), TestContext.Current.CancellationToken));
        fresh.CanDeliver.ShouldBeFalse();
        fresh.Generation.ShouldBeGreaterThan(unblocked.Generation);
    }

    [Fact]
    public void SetBlocked_ConnectedChannel_FencesOldSendAndDoesNotReviveAfterUnblock()
    {
        using var context = new MailboxTestContext();
        var relation = context.Connect();
        var envelope = context.Envelope(relation);
        Require(context.Store.Send(Alice, envelope, TestContext.Current.CancellationToken));
        var block = Require(context.Store.SetBlocked(Bob, Alice.MailboxId, relation.Generation, true, TestContext.Current.CancellationToken));
        context.Store.GetReceipt(Alice, envelope.MessageId, TestContext.Current.CancellationToken)!.State.ShouldBe(MailboxReceiptState.Blocked);
        Require(context.Store.SetBlocked(Bob, Alice.MailboxId, block.Generation, false, TestContext.Current.CancellationToken));
        context.Store.ReadPending(Bob, null, 10, TestContext.Current.CancellationToken).Items.ShouldBeEmpty();
        context.Store.Send(Alice, context.Envelope(relation), TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Conflict);
    }

    [Fact]
    public void SetBlocked_BothParticipants_OnlyOwnerClearsItsFlagAfterRestartAndPurge()
    {
        using var context = new MailboxTestContext();
        var relation = context.Connect();
        var first = Require(context.Store.SetBlocked(Bob, Alice.MailboxId, relation.Generation, true, TestContext.Current.CancellationToken));
        var both = Require(context.Store.SetBlocked(Alice, Bob.MailboxId, first.Generation, true, TestContext.Current.CancellationToken));
        context.Clock.Advance(TimeSpan.FromDays(8));
        context.Store.Sweep(100, TestContext.Current.CancellationToken);
        var restarted = context.Restart();
        restarted.GetContactRequest(Alice, relation.Request.Locator, TestContext.Current.CancellationToken).ShouldBeNull();
        var clearedAlice = Require(restarted.SetBlocked(Alice, Bob.MailboxId, both.Generation, false, TestContext.Current.CancellationToken));
        clearedAlice.IsBlockedByMailbox.ShouldBeFalse();
        clearedAlice.IsBlockedByPeer.ShouldBeTrue();
        restarted.GetContactChannel(Bob, Alice.MailboxId, TestContext.Current.CancellationToken)!.IsBlockedByMailbox.ShouldBeTrue();
    }
    [Fact]
    public void RequestContact_RetryAfterBlock_ReturnsCurrentRelationTimestamp()
    {
        using var context = new MailboxTestContext();
        Require(context.Store.PutCard(Bob, context.Card(), TestContext.Current.CancellationToken));
        var input = context.Submission();
        var request = Require(context.Store.RequestContact(Alice, input, TestContext.Current.CancellationToken));
        context.Clock.Advance(TimeSpan.FromSeconds(10));
        Require(context.Store.SetBlocked(Bob, Alice.MailboxId, request.Generation, true, TestContext.Current.CancellationToken));
        var retried = Require(context.Store.RequestContact(Alice, input, TestContext.Current.CancellationToken));
        retried.IsBlocked.ShouldBeTrue();
        retried.UpdatedAt.ShouldBe(context.Clock.GetUtcNow());
    }
}
