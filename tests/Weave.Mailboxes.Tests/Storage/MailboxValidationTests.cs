using Weave.Contacts;
using static Weave.Mailboxes.Tests.Storage.MailboxTestContext;

namespace Weave.Mailboxes.Tests.Storage;

public sealed class MailboxValidationTests
{
    [Theory]
    [InlineData("version")]
    [InlineData("generation")]
    [InlineData("uuid")]
    [InlineData("binding")]
    [InlineData("lifetime")]
    [InlineData("encoding")]
    [InlineData("future")]
    [InlineData("old")]
    public void Send_InvalidAdmission_DoesNotCreateReceipt(string mutation)
    {
        using var context = new MailboxTestContext();
        var relation = context.Connect();
        var payload = context.Payload();
        var created = payload.CreatedAt;
        var expires = payload.ExpiresAt;
        var id = payload.MessageId;
        var encoding = payload.PayloadEncoding;
        var expected = MailboxError.Invalid;
        if (mutation == "uuid")
            id = Guid.NewGuid();
        if (mutation == "binding")
            created = created.AddSeconds(1);
        if (mutation == "lifetime")
            expires = created.AddHours(25);
        if (mutation == "encoding")
            encoding = "";
        if (mutation == "future")
        { created = created.AddSeconds(31); id = Guid.CreateVersion7(created); }
        if (mutation == "old")
        { created = created.AddMinutes(-6); id = Guid.CreateVersion7(created); expected = MailboxError.Expired; }
        payload = new(id, created, expires, encoding, payload.Bytes.AsSpan());
        var envelope = context.Envelope(relation, payload);
        if (mutation == "version")
            envelope = envelope with { Version = 2 };
        if (mutation == "generation")
        { envelope = envelope with { ContactGeneration = relation.Generation + 1 }; expected = MailboxError.Conflict; }
        context.Store.Send(Alice, envelope, TestContext.Current.CancellationToken).Error.ShouldBe(expected);
        context.Store.GetReceipt(Alice, envelope.MessageId, TestContext.Current.CancellationToken).ShouldBeNull();
    }

    [Fact]
    public void PutCard_RevokedOrExpiredCard_DeniesNewRequestButNotExistingContact()
    {
        using var context = new MailboxTestContext();
        var relation = context.Connect();
        var revoked = context.Card() with { RevokedAt = context.Clock.GetUtcNow() };
        Require(context.Store.PutCard(Bob, revoked, TestContext.Current.CancellationToken));
        context.Store.FindCard(revoked.CardId, Alice, TestContext.Current.CancellationToken).ShouldBeNull();
        context.Store.RequestContact(Eve, context.Submission(revoked), TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Unavailable);
        Require(context.Store.Send(Alice, context.Envelope(relation), TestContext.Current.CancellationToken));
        context.Store.PutCard(Bob, context.Card(), TestContext.Current.CancellationToken).Error.ShouldBe(MailboxError.Conflict);
    }

    [Fact]
    public void RequestContact_DuplicateChangedInputs_ConflictsWithoutExtraPayload()
    {
        using var context = new MailboxTestContext();
        Require(context.Store.PutCard(Bob, context.Card(), TestContext.Current.CancellationToken));
        var request = context.Submission();
        Require(context.Store.RequestContact(Alice, request, TestContext.Current.CancellationToken));
        context.Store.RequestContact(Alice, request with { Payload = context.Payload("changed") }, TestContext.Current.CancellationToken)
            .Error.ShouldBe(MailboxError.Conflict);
        context.Store.ReadPending(Bob, null, 10, TestContext.Current.CancellationToken).Items.Length.ShouldBe(1);
        var independentlyScoped = Require(context.Store.RequestContact(Eve, request, TestContext.Current.CancellationToken));
        independentlyScoped.Request.RequestId.ShouldBe(request.RequestId);
        independentlyScoped.Request.RequesterMailboxId.ShouldBe(Eve.MailboxId);
        context.Store.ReadPending(Bob, null, 10, TestContext.Current.CancellationToken).Items.Length.ShouldBe(2);
    }

    [Fact]
    public void DecideContact_ExactRequestExpiry_DeniesLateDecisionAndClearsBody()
    {
        using var context = new MailboxTestContext();
        Require(context.Store.PutCard(Bob, context.Card(), TestContext.Current.CancellationToken));
        var request = Require(context.Store.RequestContact(Alice,
            context.Submission(payload: context.Payload(lifetime: TimeSpan.FromSeconds(1))), TestContext.Current.CancellationToken));
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        context.Store.DecideContact(Bob, new(request.Request.Locator, request.Generation, ContactStatus.Accepted), TestContext.Current.CancellationToken)
            .Error.ShouldBe(MailboxError.Expired);
        context.Store.ReadPending(Bob, null, 10, TestContext.Current.CancellationToken).Items.ShouldBeEmpty();
        context.Store.Sweep(100, TestContext.Current.CancellationToken).ShouldBeGreaterThan(0);
        context.Scalar("SELECT count(*) FROM mailbox_messages WHERE payload IS NOT NULL").ShouldBe(0);
    }
}
