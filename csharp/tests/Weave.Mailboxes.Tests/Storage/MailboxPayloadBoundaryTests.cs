using Weave.Contacts;
using static Weave.Mailboxes.Tests.Storage.MailboxTestContext;

namespace Weave.Mailboxes.Tests.Storage;

public sealed class MailboxPayloadBoundaryTests
{
    [Fact]
    public void RequestContact_InvalidEncoding_ReturnsTypedFailureBeforeFingerprinting()
    {
        using var context = new MailboxTestContext();
        Require(context.Store.PutCard(Bob, context.Card(), TestContext.Current.CancellationToken));
        var payload = context.Payload();
        var invalid = new MailboxPayload(payload.MessageId, payload.CreatedAt, payload.ExpiresAt, null!, payload.Bytes.AsSpan());
        context.Store.RequestContact(Alice, context.Submission(payload: invalid), TestContext.Current.CancellationToken)
            .Error.ShouldBe(MailboxError.Invalid);
        context.Scalar("SELECT count(*) FROM mailbox_messages").ShouldBe(0);
    }

    [Fact]
    public void DecideContact_InvalidReplyEncoding_RollsBackAndReturnsTypedFailure()
    {
        using var context = new MailboxTestContext();
        var request = context.Request();
        var payload = context.Payload();
        var invalid = new MailboxPayload(payload.MessageId, payload.CreatedAt, payload.ExpiresAt, null!, payload.Bytes.AsSpan());
        context.Store.DecideContact(Bob, new(request.Request.Locator, request.Generation, ContactStatus.Accepted, invalid),
            TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Invalid);
        context.Store.GetContactRequest(Alice, request.Request.Locator, TestContext.Current.CancellationToken)!.Status.ShouldBe(ContactStatus.Pending);
    }
}
