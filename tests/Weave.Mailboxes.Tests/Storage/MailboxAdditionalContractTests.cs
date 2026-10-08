using Weave.Contacts;
using static Weave.Mailboxes.Tests.Storage.MailboxTestContext;

namespace Weave.Mailboxes.Tests.Storage;

public sealed class MailboxAdditionalContractTests
{
    [Fact]
    public void Send_AcceptedPair_AllowsReverseDirectionWithIndependentOutbox()
    {
        using var context = new MailboxTestContext();
        var relation = context.Connect();
        var envelope = context.Envelope(relation, recipient: Alice);
        Require(context.Store.Send(Bob, envelope, TestContext.Current.CancellationToken));
        context.Store.ReadPending(Alice, null, 10, TestContext.Current.CancellationToken).Items.Single().SenderMailboxId.ShouldBe(Bob.MailboxId);
        context.Store.GetReceipt(Alice, envelope.MessageId, TestContext.Current.CancellationToken).ShouldBeNull();
        context.Store.GetReceipt(Bob, envelope.MessageId, TestContext.Current.CancellationToken)!.State.ShouldBe(MailboxReceiptState.Pending);
        Require(context.Store.Acknowledge(Alice, Bob.MailboxId, envelope.MessageId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RequestContact_UnlistedCardKnownById_CanRemainPending()
    {
        using var context = new MailboxTestContext();
        var request = context.Request(context.Card("shared-private", ContactVisibility.Unlisted));
        request.Request.Status.ShouldBe(ContactStatus.Pending);
        request.Request.RequesterMailboxId.ShouldBe(Alice.MailboxId);
        request.Request.RecipientMailboxId.ShouldBe(Bob.MailboxId);
        context.Store.FindCard(request.Request.CardId, Alice, TestContext.Current.CancellationToken).ShouldBeNull();
    }

    [Fact]
    public void PutCard_ExactExpiry_DeniesFreshEstablishmentOnly()
    {
        using var context = new MailboxTestContext();
        var card = context.Card() with { ExpiresAt = context.Clock.GetUtcNow().AddSeconds(1) };
        var request = context.Request(card);
        var relation = Require(context.Store.DecideContact(Bob,
            new(request.Request.Locator, request.Generation, ContactStatus.Accepted), TestContext.Current.CancellationToken));
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        context.Store.FindCard(card.CardId, null, TestContext.Current.CancellationToken).ShouldBeNull();
        context.Store.RequestContact(Eve, context.Submission(card), TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Unavailable);
        Require(context.Store.Send(Alice, context.Envelope(relation), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DecideContact_DelayedOldGeneration_CannotChangeFreshRequest()
    {
        using var context = new MailboxTestContext();
        var old = context.Request();
        var blocked = Require(context.Store.SetBlocked(Bob, Alice.MailboxId, old.Generation, true, TestContext.Current.CancellationToken));
        Require(context.Store.SetBlocked(Bob, Alice.MailboxId, blocked.Generation, false, TestContext.Current.CancellationToken));
        var fresh = Require(context.Store.RequestContact(Alice, context.Submission(), TestContext.Current.CancellationToken));
        context.Store.DecideContact(Bob, new(old.Request.Locator, old.Generation, ContactStatus.Accepted),
            TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Conflict);
        context.Store.GetContactRequest(Alice, fresh.Request.Locator, TestContext.Current.CancellationToken)!.Status.ShouldBe(ContactStatus.Pending);
        context.Store.Send(Alice, context.Envelope(fresh), TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Forbidden);
    }

    [Fact]
    public void DecideContact_ReplyIdConflictsWithExistingMessage_RollsBackDecision()
    {
        using var context = new MailboxTestContext();
        var request = context.Request();
        var reply = context.Payload("challenge");
        Require(context.Store.DecideContact(Bob,
            new(request.Request.Locator, request.Generation, ContactStatus.NeedsAction, reply), TestContext.Current.CancellationToken));
        var changed = new MailboxPayload(reply.MessageId, reply.CreatedAt, reply.ExpiresAt, reply.PayloadEncoding, [42]);
        context.Store.DecideContact(Bob, new(request.Request.Locator, request.Generation, ContactStatus.Accepted, changed),
            TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Conflict);
        context.Store.GetContactRequest(Alice, request.Request.Locator, TestContext.Current.CancellationToken)!.Status.ShouldBe(ContactStatus.NeedsAction);
    }

    [Fact]
    public void ListContactsAndReceipts_Paging_IsBoundToParticipantAndCollection()
    {
        using var context = new MailboxTestContext();
        context.Request();
        context.Request(requester: Eve);
        var first = context.Store.ListContactRequests(Bob, null, 1, TestContext.Current.CancellationToken);
        first.Items.Length.ShouldBe(1);
        first.NextCursor.ShouldNotBeNull();
        context.Store.ListContactRequests(Bob, first.NextCursor, 1, TestContext.Current.CancellationToken).Items.Length.ShouldBe(1);
        context.Store.ListContactRequests(Alice, first.NextCursor, 1, TestContext.Current.CancellationToken).Items.ShouldBeEmpty();
        context.Store.ReadPending(Bob, first.NextCursor, 1, TestContext.Current.CancellationToken).Items.ShouldBeEmpty();
        var request = context.Store.ListContactRequests(Alice, null, 10, TestContext.Current.CancellationToken).Items.Single();
        var relation = Require(context.Store.DecideContact(Bob,
            new(request.Locator, request.Generation, ContactStatus.Accepted), TestContext.Current.CancellationToken));
        Require(context.Store.Send(Alice, context.Envelope(relation), TestContext.Current.CancellationToken));
        var receipts = context.Store.ListReceipts(Alice, null, 1, TestContext.Current.CancellationToken);
        receipts.Items.Length.ShouldBe(1);
        receipts.NextCursor.ShouldNotBeNull();
        context.Store.ListReceipts(Alice, receipts.NextCursor, 1, TestContext.Current.CancellationToken).Items.Length.ShouldBe(1);
        context.Store.ListReceipts(Bob, receipts.NextCursor, 1, TestContext.Current.CancellationToken).Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("cards")]
    [InlineData("channels")]
    [InlineData("requests")]
    public void Admission_ConfiguredRowBound_DeniesNewRowsWithoutAffectingExisting(string bound)
    {
        using var context = new MailboxTestContext(o => bound switch
        {
            "cards" => o with { MaximumCards = 1 },
            "channels" => o with { MaximumChannels = 1 },
            _ => o with { MaximumRequestRows = 1 }
        });
        var request = context.Request();
        if (bound == "cards")
            context.Store.PutCard(Bob, context.Card("extra"), TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Capacity);
        else
            context.Store.RequestContact(Eve, context.Submission(), TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Capacity);
        context.Store.GetContactRequest(Alice, request.Request.Locator, TestContext.Current.CancellationToken)!.Status.ShouldBe(ContactStatus.Pending);
        context.Store.ReadPending(Bob, null, 10, TestContext.Current.CancellationToken).Items.Length.ShouldBe(1);
    }

    [Fact]
    public void Sweep_BoundedBatch_LeavesLiveConnectionAndRemovesTerminalMetadata()
    {
        using var context = new MailboxTestContext();
        var relation = context.Connect();
        for (var i = 0; i < 4; i++)
            Require(context.Store.Send(Alice, context.Envelope(relation), TestContext.Current.CancellationToken));
        context.Clock.Advance(TimeSpan.FromDays(8));
        context.Store.Sweep(1, TestContext.Current.CancellationToken).ShouldBe(1);
        context.Scalar("SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL").ShouldBe(3);
        while (context.Store.Sweep(2, TestContext.Current.CancellationToken) != 0)
        { }
        context.Scalar("SELECT count(*) FROM mailbox_messages").ShouldBe(0);
        context.Store.GetContactRequest(Alice, relation.Request.Locator, TestContext.Current.CancellationToken)!.Status.ShouldBe(ContactStatus.Accepted);
        Require(context.Store.Send(Alice, context.Envelope(relation), TestContext.Current.CancellationToken));
    }
}
