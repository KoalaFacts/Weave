using Weave.Contacts;
using static Weave.Mailboxes.Tests.Storage.MailboxTestContext;

namespace Weave.Mailboxes.Tests.Storage;

public sealed class MailboxAdmissionBoundaryTests
{
    [Fact]
    public void RequestContact_DefaultCardId_ReturnsInvalidWithoutWriting()
    {
        using var context = new MailboxTestContext();
        var request = context.Submission() with { CardId = default };
        context.Store.RequestContact(Alice, request, TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Invalid);
        context.Scalar("SELECT count(*) FROM contact_requests").ShouldBe(0);
    }

    [Fact]
    public void RequestContact_NoncanonicalTimestampId_CannotCreateAnAlias()
    {
        using var context = new MailboxTestContext();
        Require(context.Store.PutCard(Bob, context.Card(), TestContext.Current.CancellationToken));
        var request = context.Submission();
        Require(context.Store.RequestContact(Alice, request, TestContext.Current.CancellationToken));
        var alias = request with { RequestId = new(Guid.Parse(request.RequestId.Value).ToString("B")) };
        context.Store.RequestContact(Alice, alias, TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Invalid);
    }

    [Fact]
    public void RequestContact_OversizedMethod_ReturnsInvalidBeforeRetainingAnything()
    {
        using var context = new MailboxTestContext();
        Require(context.Store.PutCard(Bob, context.Card(), TestContext.Current.CancellationToken));
        context.Store.RequestContact(Alice, context.Submission() with { MethodId = new string('x', 129) },
            TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Invalid);
        context.Scalar("SELECT count(*) FROM contact_requests").ShouldBe(0);
    }

    [Fact]
    public void Send_ExpiredPendingBody_ReleasesActualBlobBytesBeforeAdmittingAnother()
    {
        using var context = new MailboxTestContext(o => o with { MaximumPendingBytes = 12 });
        var relation = context.Connect();
        Require(context.Store.Send(Alice, context.Envelope(relation, context.Payload(lifetime: TimeSpan.FromSeconds(1))),
            TestContext.Current.CancellationToken));
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        Require(context.Store.Send(Alice, context.Envelope(relation), TestContext.Current.CancellationToken));
        context.Scalar("SELECT COALESCE(sum(length(payload)),0) FROM mailbox_messages").ShouldBe(12);
    }

    [Fact]
    public void RequestContact_ExpiredHandshake_ReleasesActualBlobBytesBeforeFreshRequest()
    {
        using var context = new MailboxTestContext(o => o with { MaximumPendingBytes = 12 });
        Require(context.Store.PutCard(Bob, context.Card(), TestContext.Current.CancellationToken));
        Require(context.Store.RequestContact(Alice, context.Submission(payload: context.Payload(lifetime: TimeSpan.FromSeconds(1))),
            TestContext.Current.CancellationToken));
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        Require(context.Store.RequestContact(Eve, context.Submission(), TestContext.Current.CancellationToken));
        context.Scalar("SELECT COALESCE(sum(length(payload)),0) FROM mailbox_messages").ShouldBe(12);
    }
}
