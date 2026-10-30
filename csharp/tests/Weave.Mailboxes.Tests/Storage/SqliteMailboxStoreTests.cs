using Weave.Contacts;
using static Weave.Mailboxes.Tests.Storage.MailboxTestContext;

namespace Weave.Mailboxes.Tests.Storage;

public sealed class SqliteMailboxStoreTests
{
    [Fact]
    public void PutCard_PublicAndUnlisted_AreIndependentlyScoped()
    {
        using var context = new MailboxTestContext();
        var card = context.Card();
        Require(context.Store.PutCard(Bob, card, TestContext.Current.CancellationToken)).CardId.ShouldBe(card.CardId);
        var privateCard = context.Card("private", ContactVisibility.Unlisted);
        Require(context.Store.PutCard(Bob, privateCard, TestContext.Current.CancellationToken));
        context.Store.FindCard(card.CardId, null, TestContext.Current.CancellationToken)!.OwnerMailboxId.ShouldBe(Bob.MailboxId);
        context.Store.FindCard(privateCard.CardId, Alice, TestContext.Current.CancellationToken).ShouldBeNull();
        context.Store.FindCard(privateCard.CardId, Bob, TestContext.Current.CancellationToken)!.Visibility.ShouldBe(ContactVisibility.Unlisted);
        context.Store.PutCard(Alice, card, TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Forbidden);
    }

    [Fact]
    public void RequestContact_PublicCard_PendingWithParticipantScopedMetadata()
    {
        using var context = new MailboxTestContext();
        var request = context.Request();
        request.Request.Status.ShouldBe(ContactStatus.Pending);
        request.CanDeliver.ShouldBeFalse();
        foreach (var participant in new[] { Alice, Bob })
        {
            context.Store.ListContactRequests(participant, null, 10, TestContext.Current.CancellationToken).Items.Single().RequestId.ShouldBe(request.Request.RequestId);
            context.Store.GetContactRequest(participant, request.Request.Locator, TestContext.Current.CancellationToken)!.Status.ShouldBe(ContactStatus.Pending);
        }
        context.Store.GetContactRequest(Eve, request.Request.Locator, TestContext.Current.CancellationToken).ShouldBeNull();
        context.Store.ListContactRequests(Eve, null, 10, TestContext.Current.CancellationToken).Items.ShouldBeEmpty();
        context.Store.ReadPending(Bob, null, 10, TestContext.Current.CancellationToken).Items.Single().Envelope.MessageId.ShouldBe(request.Request.RequestMessageId!.Value);
        context.Store.ReadPending(Alice, null, 10, TestContext.Current.CancellationToken).Items.ShouldBeEmpty();
        context.Store.Send(Alice, context.Envelope(request), TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Forbidden);
    }

    [Fact]
    public void DecideContact_NeedsActionReply_UsesRecipientInboxAndCannotGrantSend()
    {
        using var context = new MailboxTestContext();
        var request = context.Request();
        var reply = context.Payload("answer this challenge");
        var decision = new ContactDecision(request.Request.Locator, request.Generation, ContactStatus.NeedsAction, reply);
        context.Store.DecideContact(Alice, decision, TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Forbidden);
        var result = Require(context.Store.DecideContact(Bob, decision, TestContext.Current.CancellationToken));
        result.Request.Status.ShouldBe(ContactStatus.NeedsAction);
        result.Request.ReplyMessageId.ShouldBe(reply.MessageId);
        context.Store.ReadPending(Alice, null, 10, TestContext.Current.CancellationToken).Items.Single().Envelope.Payload.Bytes.ShouldBe(reply.Bytes);
        context.Store.Send(Bob, context.Envelope(result, recipient: Alice), TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Forbidden);
        Require(context.Store.DecideContact(Bob, decision, TestContext.Current.CancellationToken)).Request.ReplyMessageId.ShouldBe(reply.MessageId);
        context.Store.ReadPending(Alice, null, 10, TestContext.Current.CancellationToken).Items.Length.ShouldBe(1);
    }

    [Fact]
    public void DecideContact_PublicRejection_DoesNotOpenDelivery()
    {
        using var context = new MailboxTestContext();
        var request = context.Request();
        var rejected = Require(context.Store.DecideContact(Bob,
            new(request.Request.Locator, request.Generation, ContactStatus.Rejected, context.Payload("no")), TestContext.Current.CancellationToken));
        rejected.CanDeliver.ShouldBeFalse();
        context.Store.GetContactRequest(Alice, request.Request.Locator, TestContext.Current.CancellationToken)!.Status.ShouldBe(ContactStatus.Rejected);
        context.Store.Send(Alice, context.Envelope(request), TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Forbidden);
    }

    [Fact]
    public void Send_AcceptedContact_DurableSinglePayloadAndMetadataOutbox()
    {
        using var context = new MailboxTestContext();
        var relation = context.Connect();
        var envelope = context.Envelope(relation);
        var receipt = Require(context.Store.Send(Alice, envelope, TestContext.Current.CancellationToken));
        receipt.State.ShouldBe(MailboxReceiptState.Pending);
        var restarted = context.Restart();
        var incoming = restarted.ReadPending(Bob, null, 10, TestContext.Current.CancellationToken).Items.Single();
        incoming.SenderMailboxId.ShouldBe(Alice.MailboxId);
        incoming.Envelope.Payload.Bytes.ShouldBe(envelope.Payload.Bytes);
        restarted.GetReceipt(Alice, envelope.MessageId, TestContext.Current.CancellationToken).ShouldBe(receipt);
        restarted.GetReceipt(Bob, envelope.MessageId, TestContext.Current.CancellationToken).ShouldBeNull();
        restarted.ReadPending(Eve, null, 10, TestContext.Current.CancellationToken).Items.ShouldBeEmpty();
        restarted.ListReceipts(Alice, null, 20, TestContext.Current.CancellationToken).Items.ShouldContain(receipt);
        context.Scalar("SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL").ShouldBe(1);
        context.Scalar("SELECT count(*) FROM pragma_table_info('contact_requests') WHERE name LIKE '%body%' OR name = 'payload'").ShouldBe(0);
    }

    [Fact]
    public void Send_IdenticalAndConflictingRetries_PreserveFirstAdmission()
    {
        using var context = new MailboxTestContext();
        var relation = context.Connect();
        var envelope = context.Envelope(relation);
        var first = Require(context.Store.Send(Alice, envelope, TestContext.Current.CancellationToken));
        Require(context.Store.Send(Alice, envelope, TestContext.Current.CancellationToken)).ShouldBe(first);
        var changed = envelope with
        {
            Payload = new(envelope.MessageId, envelope.CreatedAt, envelope.ExpiresAt,
            envelope.PayloadEncoding, [9])
        };
        context.Store.Send(Alice, changed, TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Conflict);
        context.Store.ReadPending(Bob, null, 10, TestContext.Current.CancellationToken).Items.Length.ShouldBe(1);
    }

    [Fact]
    public void Acknowledge_RecipientOnly_IdempotentlyDeletesBody()
    {
        using var context = new MailboxTestContext();
        var relation = context.Connect();
        var envelope = context.Envelope(relation);
        Require(context.Store.Send(Alice, envelope, TestContext.Current.CancellationToken));
        context.Store.Acknowledge(Eve, Alice.MailboxId, envelope.MessageId, TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Forbidden);
        context.Store.Acknowledge(Alice, Alice.MailboxId, envelope.MessageId, TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Forbidden);
        var ack = Require(context.Store.Acknowledge(Bob, Alice.MailboxId, envelope.MessageId, TestContext.Current.CancellationToken));
        ack.State.ShouldBe(MailboxReceiptState.Acknowledged);
        Require(context.Store.Acknowledge(Bob, Alice.MailboxId, envelope.MessageId, TestContext.Current.CancellationToken)).ShouldBe(ack);
        Require(context.Store.Send(Alice, envelope, TestContext.Current.CancellationToken)).State.ShouldBe(MailboxReceiptState.Acknowledged);
        context.Store.ReadPending(Bob, null, 10, TestContext.Current.CancellationToken).Items.ShouldBeEmpty();
        context.Scalar("SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL").ShouldBe(0);
    }

    [Fact]
    public void ReadPending_AtExactExpiry_HidesBeforeSweepAndAcknowledgesAsExpired()
    {
        using var context = new MailboxTestContext();
        var relation = context.Connect();
        var envelope = context.Envelope(relation, context.Payload(lifetime: TimeSpan.FromSeconds(5)));
        Require(context.Store.Send(Alice, envelope, TestContext.Current.CancellationToken));
        context.Clock.Advance(TimeSpan.FromSeconds(5));
        context.Store.ReadPending(Bob, null, 10, TestContext.Current.CancellationToken).Items.ShouldBeEmpty();
        context.Store.GetReceipt(Alice, envelope.MessageId, TestContext.Current.CancellationToken)!.State.ShouldBe(MailboxReceiptState.Expired);
        Require(context.Store.Acknowledge(Bob, Alice.MailboxId, envelope.MessageId, TestContext.Current.CancellationToken)).State.ShouldBe(MailboxReceiptState.Expired);
        context.Scalar("SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL").ShouldBe(0);
    }

    [Fact]
    public void Sweep_AfterRetention_RejectsPurgedOldIds()
    {
        using var context = new MailboxTestContext();
        var relation = context.Connect();
        var envelope = context.Envelope(relation);
        Require(context.Store.Send(Alice, envelope, TestContext.Current.CancellationToken));
        Require(context.Store.Acknowledge(Bob, Alice.MailboxId, envelope.MessageId, TestContext.Current.CancellationToken));
        context.Clock.Advance(TimeSpan.FromDays(8));
        context.Store.Sweep(100, TestContext.Current.CancellationToken).ShouldBeGreaterThan(0);
        context.Store.GetReceipt(Alice, envelope.MessageId, TestContext.Current.CancellationToken).ShouldBeNull();
        context.Store.Send(Alice, envelope, TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Expired);
        var forged = envelope with
        {
            Payload = new(envelope.MessageId, context.Clock.GetUtcNow(),
            context.Clock.GetUtcNow().AddMinutes(1), envelope.PayloadEncoding, envelope.Payload.Bytes.AsSpan())
        };
        context.Store.Send(Alice, forged, TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Invalid);
    }

    [Fact]
    public void ReadPending_Pages_DoNotAcknowledgeOrLeakAcrossMailboxes()
    {
        using var context = new MailboxTestContext();
        var relation = context.Connect();
        for (var i = 0; i < 3; i++)
            Require(context.Store.Send(Alice, context.Envelope(relation), TestContext.Current.CancellationToken));
        var first = context.Store.ReadPending(Bob, null, 2, TestContext.Current.CancellationToken);
        first.Items.Length.ShouldBe(2);
        first.NextCursor.ShouldNotBeNull();
        var second = context.Store.ReadPending(Bob, first.NextCursor, 2, TestContext.Current.CancellationToken);
        second.Items.Length.ShouldBe(1);
        second.NextCursor.ShouldBeNull();
        context.Store.ReadPending(Eve, first.NextCursor, 2, TestContext.Current.CancellationToken).Items.ShouldBeEmpty();
        context.Store.ReadPending(Bob, null, 10, TestContext.Current.CancellationToken).Items.Length.ShouldBe(3);
        context.Store.ReadPending(Bob, "invalid", 10, TestContext.Current.CancellationToken).Items.ShouldBeEmpty();
        context.Store.ReadPending(Bob, null, 0, TestContext.Current.CancellationToken).Items.ShouldBeEmpty();
    }
}
