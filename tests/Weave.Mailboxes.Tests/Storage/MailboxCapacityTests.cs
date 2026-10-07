using Weave.Contacts;
using static Weave.Mailboxes.Tests.Storage.MailboxTestContext;

namespace Weave.Mailboxes.Tests.Storage;

public sealed class MailboxCapacityTests
{
    [Fact]
    public void Send_PendingQuota_AcknowledgeReleasesExactlyOnce()
    {
        using var context = new MailboxTestContext(o => o with { MaximumPendingMessagesPerMailbox = 1 });
        var relation = context.Connect();
        var first = context.Envelope(relation);
        Require(context.Store.Send(Alice, first, TestContext.Current.CancellationToken));
        context.Store.Send(Alice, context.Envelope(relation), TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Capacity);
        for (var i = 0; i < 3; i++) Require(context.Store.Acknowledge(Bob, Alice.MailboxId, first.MessageId, TestContext.Current.CancellationToken));
        Require(context.Store.Send(Alice, context.Envelope(relation), TestContext.Current.CancellationToken));
        context.Store.Send(Alice, context.Envelope(relation), TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Capacity);
    }

    [Fact]
    public void RequestContact_RecipientQuota_RollsBackMetadataAndEnvelopeTogether()
    {
        using var context = new MailboxTestContext(o => o with { MaximumPendingRequestsPerRecipient = 1 });
        var first = context.Request();
        var second = context.Submission();
        context.Store.RequestContact(Eve, second, TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Capacity);
        context.Store.GetContactRequest(Eve, new(Eve.MailboxId, second.RequestId), TestContext.Current.CancellationToken).ShouldBeNull();
        context.Store.GetContactChannel(Eve, Bob.MailboxId, TestContext.Current.CancellationToken).ShouldBeNull();
        context.Store.ReadPending(Bob, null, 10, TestContext.Current.CancellationToken).Items.Length.ShouldBe(1);
        Require(context.Store.DecideContact(Bob, new(first.Request.Locator, first.Generation, ContactStatus.Rejected), TestContext.Current.CancellationToken));
        Require(context.Store.RequestContact(Eve, second, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DecideContact_ReplyQuotaFull_RollsBackDecision()
    {
        using var context = new MailboxTestContext(o => o with { MaximumPendingBytes = 12 });
        var request = context.Request();
        var failed = context.Store.DecideContact(Bob,
            new(request.Request.Locator, request.Generation, ContactStatus.Accepted, context.Payload("long reply")), TestContext.Current.CancellationToken);
        failed.Error.ShouldBe(MailboxError.Capacity);
        context.Store.GetContactRequest(Alice, request.Request.Locator, TestContext.Current.CancellationToken)!.Status.ShouldBe(ContactStatus.Pending);
        context.Store.ReadPending(Alice, null, 10, TestContext.Current.CancellationToken).Items.ShouldBeEmpty();
    }

    [Fact]
    public void Send_RetainedRowQuota_DoesNotRefuseAckOrCleanup()
    {
        using var context = new MailboxTestContext(o => o with { MaximumMessageRows = 2 });
        var relation = context.Connect();
        var envelope = context.Envelope(relation);
        Require(context.Store.Send(Alice, envelope, TestContext.Current.CancellationToken));
        context.Store.Send(Alice, context.Envelope(relation), TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Capacity);
        Require(context.Store.Acknowledge(Bob, Alice.MailboxId, envelope.MessageId, TestContext.Current.CancellationToken));
        context.Clock.Advance(TimeSpan.FromDays(8));
        context.Store.Sweep(100, TestContext.Current.CancellationToken).ShouldBeGreaterThan(0);
        Require(context.Store.Send(Alice, context.Envelope(relation), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RequestContact_MailboxCapacity_DoesNotCreateAnUnboundedIdentity()
    {
        using var context = new MailboxTestContext(o => o with { MaximumMailboxes = 2 });
        context.Request();
        context.Store.RequestContact(Eve, context.Submission(), TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Capacity);
        context.Scalar("SELECT count(*) FROM mailbox_owners").ShouldBe(2);
    }
}
