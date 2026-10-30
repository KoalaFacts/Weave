using Weave.Contacts;
using static Weave.Mailboxes.Tests.Storage.MailboxTestContext;

namespace Weave.Mailboxes.Tests.Storage;

public sealed class MailboxContactHistoryTests
{
    [Theory]
    [InlineData(ContactStatus.Pending, ContactStatus.Expired)]
    [InlineData(ContactStatus.NeedsAction, ContactStatus.Expired)]
    [InlineData(ContactStatus.Accepted, ContactStatus.Accepted)]
    [InlineData(ContactStatus.Rejected, ContactStatus.Rejected)]
    public void History_ExpirySweepAndRestart_PreservesMetadataWithoutMessageBodies(ContactStatus decision, ContactStatus expected)
    {
        using var context = new MailboxTestContext();
        Require(context.Store.PutCard(Bob, context.Card(), Ct));
        var submission = context.Submission();
        var relation = Require(context.Store.RequestContact(Alice, submission, Ct));
        if (decision != ContactStatus.Pending)
            Require(context.Store.DecideContact(Bob, new(relation.Request.Locator, relation.Generation, decision), Ct));

        context.Clock.Advance(TimeSpan.FromMinutes(20) - TimeSpan.FromTicks(1));
        context.Store.GetContactRequest(Bob, relation.Request.Locator, Ct)!.Status.ShouldBe(decision);
        context.Clock.Advance(TimeSpan.FromTicks(1));
        var expired = context.Store.GetContactRequest(Bob, relation.Request.Locator, Ct)!;
        expired.Status.ShouldBe(expected);
        context.Store.ReadPending(Bob, null, 10, Ct).Items.ShouldBeEmpty();
        context.Scalar("SELECT status FROM contact_requests").ShouldBe((int)decision);
        if (expected == ContactStatus.Expired)
            context.Store.DecideContact(Bob, new(relation.Request.Locator, relation.Generation, ContactStatus.Accepted), Ct)
                .Error.ShouldBe(MailboxError.Expired);

        context.Clock.Advance(TimeSpan.FromDays(8));
        while (context.Store.Sweep(100, Ct) != 0)
        { }
        context.Scalar("SELECT count(*) FROM mailbox_messages").ShouldBe(0);
        context.Scalar("SELECT count(*) FROM contact_requests").ShouldBe(1);
        var restarted = context.Restart();
        foreach (var participant in new[] { Alice, Bob })
        {
            restarted.GetContactRequest(participant, relation.Request.Locator, Ct).ShouldBe(expired);
            restarted.ListContactRequests(participant, null, 10, Ct).Items.Single().ShouldBe(expired);
        }
        restarted.ListContactRequests(Eve, null, 10, Ct).Items.ShouldBeEmpty();
        restarted.GetContactRequest(Eve, relation.Request.Locator, Ct).ShouldBeNull();
        restarted.GetContactChannel(Alice, Bob.MailboxId, Ct)!.CurrentRequest.ShouldBe(relation.Request.Locator);
        Require(restarted.RequestContact(Alice, submission, Ct)).Request.ShouldBe(expired);
        context.Scalar("SELECT count(*) FROM mailbox_messages").ShouldBe(0);
        context.Scalar("SELECT count(*) FROM contact_requests").ShouldBe(1);
    }

    [Fact]
    public void History_ExpiredAndAcceptedRequests_PaginatesWithoutHidingEither()
    {
        using var context = new MailboxTestContext();
        var first = context.Request();
        var second = context.Request(requester: Eve);
        Require(context.Store.DecideContact(Bob, new(second.Request.Locator, second.Generation, ContactStatus.Accepted), Ct));
        context.Clock.Advance(TimeSpan.FromDays(8));
        while (context.Store.Sweep(100, Ct) != 0)
        { }
        var page = context.Store.ListContactRequests(Bob, null, 1, Ct);
        page.Items.Single().RequestId.ShouldBe(first.Request.RequestId);
        page.Items.Single().Status.ShouldBe(ContactStatus.Expired);
        page.NextCursor.ShouldNotBeNull();
        var last = context.Store.ListContactRequests(Bob, page.NextCursor, 1, Ct);
        last.Items.Single().RequestId.ShouldBe(second.Request.RequestId);
        last.Items.Single().Status.ShouldBe(ContactStatus.Accepted);
        last.NextCursor.ShouldBeNull();
    }

    [Fact]
    public void Decision_ExpiredIsSystemState_RejectsRecipientAssignment()
    {
        using var context = new MailboxTestContext();
        var relation = context.Request();
        context.Store.DecideContact(Bob, new(relation.Request.Locator, relation.Generation, ContactStatus.Expired), Ct)
            .Error.ShouldBe(MailboxError.Invalid);
        context.Store.GetContactRequest(Bob, relation.Request.Locator, Ct)!.Status.ShouldBe(ContactStatus.Pending);
        var expired = relation with { Request = relation.Request with { Status = ContactStatus.Expired } };
        expired.ApplyRecipientDecision(new(relation.Request.Locator, relation.Generation, ContactStatus.Accepted), context.Clock.GetUtcNow())
            .Outcome.ShouldBe(ContactRelationOutcome.Expired);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
