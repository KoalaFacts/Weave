using System.Text;
using Weave.Contacts;
using Weave.Mailboxes;

namespace Weave.Mailboxes.Tests.Contacts;

public sealed class ContactPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Discover_PublicCard_DoesNotAcceptContact()
    {
        var card = Card(ContactVisibility.Public);
        var relation = Relation(card);

        var discoverable = ContactCardPolicy.CanDiscover(card, new MailboxId("requester-agent"));

        discoverable.ShouldBeTrue();
        relation.Request.Status.ShouldBe(ContactStatus.Pending);
        relation.CanDeliver.ShouldBeFalse();
    }

    [Fact]
    public void Discover_AgentWithPublicAndUnlistedCards_DoesNotLeakUnlistedCard()
    {
        var publicCard = Card(ContactVisibility.Public);
        var unlistedCard = Card(ContactVisibility.Unlisted) with
        {
            CardId = new ContactCardId("internal-team-card"),
            AudienceHint = "research-team-intranet"
        };
        ContactCard[] cards = [publicCard, unlistedCard];

        var discovered = cards.Where(card => ContactCardPolicy.CanDiscover(card, new MailboxId("requester-agent"))).ToArray();

        discovered.Select(card => card.CardId).ShouldBe([publicCard.CardId]);
    }

    [Fact]
    public void Create_MultipleLongLivedCards_PreservesIndependentExposure()
    {
        var publicCard = Card(ContactVisibility.Public) with { ExpiresAt = null };
        var privateCard = Card(ContactVisibility.Unlisted) with
        {
            CardId = new ContactCardId("permanent-intranet-card"),
            AudienceHint = "intranet",
            ExpiresAt = null
        };
        var muchLater = Now.AddYears(1);

        var publicValidity = ContactCardPolicy.Validate(publicCard, muchLater);
        var privateValidity = ContactCardPolicy.Validate(privateCard, muchLater);

        publicValidity.ShouldBe(ContactCardValidation.Valid);
        privateValidity.ShouldBe(ContactCardValidation.Valid);
        ContactCardPolicy.CanDiscover(publicCard, new MailboxId("requester-agent")).ShouldBeTrue();
        ContactCardPolicy.CanDiscover(privateCard, new MailboxId("requester-agent")).ShouldBeFalse();
        ContactCardPolicy.CanDiscover(privateCard, new MailboxId("recipient-agent")).ShouldBeTrue();
    }

    [Fact]
    public void Decide_PublicRequest_PendingRemainsPending()
    {
        var relation = Relation(Card(ContactVisibility.Public));

        var result = relation.ApplyRecipientDecision(Decision(relation, ContactStatus.Pending), Now);

        result.Outcome.ShouldBe(ContactRelationOutcome.Applied);
        result.Relation.Request.Status.ShouldBe(ContactStatus.Pending);
        result.Relation.CanDeliver.ShouldBeFalse();
    }

    [Fact]
    public void Decide_PublicRequest_RejectDoesNotGrantDelivery()
    {
        var relation = Relation(Card(ContactVisibility.Public));

        var result = relation.ApplyRecipientDecision(Decision(relation, ContactStatus.Rejected), Now);

        result.Outcome.ShouldBe(ContactRelationOutcome.Applied);
        result.Relation.Request.Status.ShouldBe(ContactStatus.Rejected);
        result.Relation.CanDeliver.ShouldBeFalse();
    }

    [Fact]
    public void Decide_ExplicitEndpointAccept_RecordsAccepted()
    {
        var relation = Relation(Card(ContactVisibility.Public));

        var result = relation.ApplyRecipientDecision(Decision(relation, ContactStatus.Accepted), Now);

        result.Outcome.ShouldBe(ContactRelationOutcome.Applied);
        result.Relation.Request.Status.ShouldBe(ContactStatus.Accepted);
        result.Relation.CanDeliver.ShouldBeTrue();
        relation.Request.Status.ShouldBe(ContactStatus.Pending);
        relation.CanDeliver.ShouldBeFalse();
    }

    [Theory]
    [InlineData(ContactVisibility.Public, ContactStatus.Pending, false)]
    [InlineData(ContactVisibility.Public, ContactStatus.NeedsAction, false)]
    [InlineData(ContactVisibility.Public, ContactStatus.Accepted, true)]
    [InlineData(ContactVisibility.Public, ContactStatus.Rejected, false)]
    [InlineData(ContactVisibility.Unlisted, ContactStatus.Pending, false)]
    [InlineData(ContactVisibility.Unlisted, ContactStatus.NeedsAction, false)]
    [InlineData(ContactVisibility.Unlisted, ContactStatus.Accepted, true)]
    [InlineData(ContactVisibility.Unlisted, ContactStatus.Rejected, false)]
    public void Decide_VisibilityAndRecipientDecision_OnlyExplicitAcceptGrantsDelivery(
        ContactVisibility visibility, ContactStatus status, bool canDeliver)
    {
        var card = Card(visibility);
        var relation = Relation(card);

        var discoverable = ContactCardPolicy.CanDiscover(card, new MailboxId("requester-agent"));
        var result = relation.ApplyRecipientDecision(Decision(relation, status), Now);

        discoverable.ShouldBe(visibility == ContactVisibility.Public);
        result.Outcome.ShouldBe(ContactRelationOutcome.Applied);
        result.Relation.Request.Status.ShouldBe(status);
        result.Relation.CanDeliver.ShouldBe(canDeliver);
    }

    [Fact]
    public void Validate_ShortCardAtExpiry_RejectsNewRequest()
    {
        var card = Card(ContactVisibility.Public) with { ExpiresAt = Now };

        var validity = ContactCardPolicy.Validate(card, Now);

        validity.ShouldBe(ContactCardValidation.Expired);
    }

    [Fact]
    public void ExpireCard_ExistingContact_IsUnchanged()
    {
        var card = Card(ContactVisibility.Public) with { ExpiresAt = Now.AddMinutes(1) };
        var accepted = Relation(card).ApplyRecipientDecision(
            new ContactDecision(new(new MailboxId("requester-agent"), new ContactRequestId("request-123")), 1, ContactStatus.Accepted), Now).Relation;

        var validity = ContactCardPolicy.Validate(card, Now.AddMinutes(1));

        validity.ShouldBe(ContactCardValidation.Expired);
        accepted.CanDeliver.ShouldBeTrue();
        accepted.Request.Status.ShouldBe(ContactStatus.Accepted);
    }

    [Fact]
    public void ChooseMethod_UnknownMethod_ReturnsUnsupported()
    {
        var card = Card(ContactVisibility.Unlisted);

        var result = ContactCardPolicy.ChooseMethod(card, "not-offered");

        result.Outcome.ShouldBe(ContactMethodChoiceOutcome.Unsupported);
        result.Method.ShouldBeNull();
    }

    [Fact]
    public void ChooseMethod_OfferedDirectMethod_ReturnsOpaqueInstructions()
    {
        var card = Card(ContactVisibility.Unlisted);

        var result = ContactCardPolicy.ChooseMethod(card, "private-direct");

        result.Outcome.ShouldBe(ContactMethodChoiceOutcome.Selected);
        result.Method.ShouldNotBeNull();
        result.Method.Endpoint.ShouldBe("https://intranet.example.test/contact");
        result.Method.Instructions.ShouldBe("network access required; use recipient-specific handshake");
    }

    [Fact]
    public void Block_AcceptedContact_OverridesDelivery()
    {
        var relation = Accept(Relation(Card(ContactVisibility.Public)));

        var result = relation.SetBlocked(true, relation.Generation, Now.AddSeconds(1));

        result.Outcome.ShouldBe(ContactRelationOutcome.Applied);
        result.Relation.IsBlocked.ShouldBeTrue();
        result.Relation.CanDeliver.ShouldBeFalse();
        result.Relation.Generation.ShouldBe(relation.Generation + 1);
        result.Relation.IsConnected.ShouldBeFalse();
    }

    [Fact]
    public void Decide_OldGenerationCannotOverrideBlock()
    {
        var relation = Relation(Card(ContactVisibility.Public));
        var oldDecision = Decision(relation, ContactStatus.Accepted);
        var blocked = relation.SetBlocked(true, relation.Generation, Now).Relation;

        var result = blocked.ApplyRecipientDecision(oldDecision, Now.AddSeconds(1));

        result.Outcome.ShouldBe(ContactRelationOutcome.GenerationConflict);
        result.Relation.ShouldBe(blocked);
        result.Relation.CanDeliver.ShouldBeFalse();
        result.Relation.IsBlocked.ShouldBeTrue();
    }

    [Fact]
    public void Unblock_PreviouslyAcceptedContact_RemainsDisconnected()
    {
        var accepted = Accept(Relation(Card(ContactVisibility.Unlisted)));
        var blocked = accepted.SetBlocked(true, accepted.Generation, Now).Relation;

        var result = blocked.SetBlocked(false, blocked.Generation, Now.AddSeconds(1));

        result.Outcome.ShouldBe(ContactRelationOutcome.Applied);
        result.Relation.IsBlocked.ShouldBeFalse();
        result.Relation.IsConnected.ShouldBeFalse();
        result.Relation.CanDeliver.ShouldBeFalse();
        result.Relation.Generation.ShouldBe(blocked.Generation + 1);
    }

    [Fact]
    public void Decide_UnblockedOldRequest_CannotRestoreContact()
    {
        var relation = Relation(Card(ContactVisibility.Public));
        var blocked = relation.SetBlocked(true, relation.Generation, Now).Relation;
        var unblocked = blocked.SetBlocked(false, blocked.Generation, Now).Relation;

        var result = unblocked.ApplyRecipientDecision(Decision(unblocked, ContactStatus.Accepted), Now);

        result.Outcome.ShouldBe(ContactRelationOutcome.NotPending);
        result.Relation.CanDeliver.ShouldBeFalse();
        result.Relation.IsConnected.ShouldBeFalse();
    }

    [Fact]
    public void Decide_BlockedCurrentGeneration_DeniesAdmission()
    {
        var relation = Relation(Card(ContactVisibility.Public));
        var blocked = relation.SetBlocked(true, relation.Generation, Now).Relation;

        var result = blocked.ApplyRecipientDecision(Decision(blocked, ContactStatus.Accepted), Now);

        result.Outcome.ShouldBe(ContactRelationOutcome.Blocked);
        result.Relation.ShouldBe(blocked);
    }

    [Fact]
    public void Decide_ExpiredPendingRequest_DoesNotGrantDelivery()
    {
        var relation = Relation(Card(ContactVisibility.Public));

        var result = relation.ApplyRecipientDecision(Decision(relation, ContactStatus.Accepted), relation.Request.ExpiresAt);

        result.Outcome.ShouldBe(ContactRelationOutcome.Expired);
        result.Relation.CanDeliver.ShouldBeFalse();
    }

    [Fact]
    public void Decide_DifferentRequestId_DoesNotChangeState()
    {
        var relation = Relation(Card(ContactVisibility.Public));
        var decision = Decision(relation, ContactStatus.Accepted) with { Request = new(relation.Request.RequesterMailboxId, new ContactRequestId("other-request")) };

        var result = relation.ApplyRecipientDecision(decision, Now);

        result.Outcome.ShouldBe(ContactRelationOutcome.RequestMismatch);
        result.Relation.ShouldBe(relation);
    }

    [Theory]
    [InlineData((ContactStatus)(-1))]
    [InlineData((ContactStatus)999)]
    public void Decide_UnsupportedStatus_DoesNotChangeState(ContactStatus status)
    {
        var relation = Relation(Card(ContactVisibility.Public));

        var result = relation.ApplyRecipientDecision(Decision(relation, status), Now);

        result.Outcome.ShouldBe(ContactRelationOutcome.InvalidDecision);
        result.Relation.ShouldBe(relation);
    }

    [Fact]
    public void Block_StaleGeneration_DoesNotChangeState()
    {
        var relation = Accept(Relation(Card(ContactVisibility.Public)));

        var result = relation.SetBlocked(true, relation.Generation - 1, Now);

        result.Outcome.ShouldBe(ContactRelationOutcome.GenerationConflict);
        result.Relation.ShouldBe(relation);
        result.Relation.CanDeliver.ShouldBeTrue();
    }

    [Fact]
    public void Validate_RevokedLongLivedCard_RejectsNewRequest()
    {
        var card = Card(ContactVisibility.Public) with { ExpiresAt = null, RevokedAt = Now };

        var validity = ContactCardPolicy.Validate(card, Now);

        validity.ShouldBe(ContactCardValidation.Revoked);
    }

    [Fact]
    public void Validate_JustBeforeExpiry_AcceptsCard()
    {
        var card = Card(ContactVisibility.Unlisted) with { ExpiresAt = Now.AddTicks(1) };

        var validity = ContactCardPolicy.Validate(card, Now);

        validity.ShouldBe(ContactCardValidation.Valid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void Validate_MalformedCard_ReturnsInvalid(int invalidCase)
    {
        var card = Card(ContactVisibility.Public);
        card = invalidCase switch
        {
            0 => card with { CardId = new ContactCardId(" ") },
            1 => card with { OwnerMailboxId = new MailboxId("") },
            2 => card with { Visibility = (ContactVisibility)999 },
            3 => card with { CreatedAt = Now.AddSeconds(1) },
            4 => card with { ExpiresAt = card.CreatedAt },
            5 => card with { Methods = [] },
            6 => card with { Methods = [card.Methods[0], card.Methods[0]] },
            7 => card with { Methods = [card.Methods[0] with { Transport = "unknown" }] },
            8 => card with { Methods = [card.Methods[0] with { Version = 0 }] },
            9 => card with { Methods = [card.Methods[0] with { Instructions = new string('x', 65537) }] },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidCase))
        };

        var validity = ContactCardPolicy.Validate(card, Now);

        validity.ShouldBe(ContactCardValidation.Invalid);
    }

    [Fact]
    public void Create_CallerMutatesMethods_CardPreservesSnapshot()
    {
        ContactMethod[] methods = [Method()];
        var card = new ContactCard(new ContactCardId("shared-card"), new MailboxId("recipient-agent"), ContactVisibility.Unlisted,
            Now.AddMinutes(-1), null, methods);
        methods[0] = Method() with { Endpoint = "https://attacker.example.test/changed" };

        var result = ContactCardPolicy.ChooseMethod(card, "private-direct");

        result.Outcome.ShouldBe(ContactMethodChoiceOutcome.Selected);
        result.Method.ShouldNotBeNull();
        result.Method.Endpoint.ShouldBe("https://intranet.example.test/contact");
    }

    [Fact]
    public void Create_CallerMutatesRequestPayload_RequestPreservesOwnedBytes()
    {
        var payload = Encoding.UTF8.GetBytes("recipient challenge response");
        var request = new ContactRequest(new ContactRequestId("request-123"), new ContactCardId("public-card"),
            new MailboxId("requester-agent"), new MailboxId("recipient-agent"), "private-direct", 1, Now, Now.AddHours(1),
            new MailboxPayload(Guid.Parse("019963a5-b280-7000-8000-000000000001"), Now, Now.AddHours(1), "utf8", payload));
        payload[0] = (byte)'X';

        var summary = request.ToSummary();

        Encoding.UTF8.GetString(request.Payload.Bytes.AsSpan()).ShouldBe("recipient challenge response");
        summary.Status.ShouldBe(ContactStatus.Pending);
        summary.RequestMessageId.ShouldBe(request.Payload.MessageId);
        summary.ReplyMessageId.ShouldBeNull();
    }

    [Fact]
    public void Decide_ReplyEnvelope_RetainsOnlyFrozenMessageReference()
    {
        var relation = Relation(Card(ContactVisibility.Public));
        var bytes = Encoding.UTF8.GetBytes("recipient-owned reply body");
        var reply = new MailboxPayload(Guid.Parse("019963a5-b280-7000-8000-000000000002"),
            Now, Now.AddMinutes(10), "utf8", bytes);
        var decision = Decision(relation, ContactStatus.NeedsAction) with { Reply = reply };
        bytes[0] = (byte)'X';

        var result = relation.ApplyRecipientDecision(decision, Now);

        result.Outcome.ShouldBe(ContactRelationOutcome.Applied);
        result.Relation.Request.ReplyMessageId.ShouldBe(reply.MessageId);
        result.Relation.Request.Status.ShouldBe(ContactStatus.NeedsAction);
        result.Relation.CanDeliver.ShouldBeFalse();
        Encoding.UTF8.GetString(reply.Bytes.AsSpan()).ShouldBe("recipient-owned reply body");
    }

    [Fact]
    public void Decide_NeedsActionThenAccept_UsesSameGeneration()
    {
        var relation = Relation(Card(ContactVisibility.Unlisted));
        var needsAction = relation.ApplyRecipientDecision(Decision(relation, ContactStatus.NeedsAction), Now).Relation;

        var result = needsAction.ApplyRecipientDecision(Decision(needsAction, ContactStatus.Accepted), Now.AddSeconds(1));

        result.Outcome.ShouldBe(ContactRelationOutcome.Applied);
        result.Relation.Request.Status.ShouldBe(ContactStatus.Accepted);
        result.Relation.CanDeliver.ShouldBeTrue();
        result.Relation.Generation.ShouldBe(relation.Generation);
    }

    [Theory]
    [InlineData(ContactStatus.Accepted)]
    [InlineData(ContactStatus.Rejected)]
    public void Decide_TerminalRequest_CannotChangeDecision(ContactStatus status)
    {
        var relation = Relation(Card(ContactVisibility.Unlisted));
        var terminal = relation.ApplyRecipientDecision(Decision(relation, status), Now).Relation;
        var newStatus = status == ContactStatus.Accepted ? ContactStatus.Rejected : ContactStatus.Accepted;

        var result = terminal.ApplyRecipientDecision(Decision(terminal, newStatus), Now.AddSeconds(1));

        result.Outcome.ShouldBe(ContactRelationOutcome.NotPending);
        result.Relation.ShouldBe(terminal);
        result.Relation.CanDeliver.ShouldBe(status == ContactStatus.Accepted);
    }

    [Fact]
    public void Create_PayloadExceedsMaximum_RejectsBeforeCopying()
    {
        var bytes = new byte[65537];

        Should.Throw<ArgumentOutOfRangeException>(() => new MailboxPayload(Guid.NewGuid(), Now,
            Now.AddHours(1), "opaque", bytes));
    }

    [Fact]
    public void Create_PayloadAtMaximum_PreservesOwnedBoundaryBytes()
    {
        var bytes = new byte[65536];
        bytes[^1] = 42;
        var payload = new MailboxPayload(Guid.NewGuid(), Now, Now.AddHours(1), "opaque", bytes);
        bytes[^1] = 99;

        payload.Bytes.Length.ShouldBe(65536);
        payload.Bytes[^1].ShouldBe((byte)42);
    }

    private static ContactCard Card(ContactVisibility visibility) =>
        new(new ContactCardId("public-card"), new MailboxId("recipient-agent"), visibility, Now.AddMinutes(-1), Now.AddHours(24), [Method()]);

    private static ContactMethod Method() => new("private-direct", 1, "direct",
        "https://intranet.example.test/contact", "network access required; use recipient-specific handshake");

    private static ContactRelation Relation(ContactCard card) =>
        new(new ContactRequestSummary(new ContactRequestId("request-123"), card.CardId, new MailboxId("requester-agent"), card.OwnerMailboxId,
            "private-direct", 1, ContactStatus.Pending, Now, Now.AddHours(24)), false, Now);

    private static ContactDecision Decision(ContactRelation relation, ContactStatus status) =>
        new(relation.Request.Locator, relation.Generation, status);

    private static ContactRelation Accept(ContactRelation relation) =>
        relation.ApplyRecipientDecision(Decision(relation, ContactStatus.Accepted), Now).Relation;
}
