using System.Collections.Immutable;
using Weave.Contacts;
using Weave.Mailboxes;

namespace Weave.Mailboxes.Tests.Contacts;

public sealed class ContactBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Block_ExhaustedGeneration_FailsWithoutWrappingEpoch()
    {
        var summary = new ContactRequestSummary(new ContactRequestId("request-epoch-limit"),
            new ContactCardId("card-epoch-limit"), new MailboxId("requester"), new MailboxId("recipient"),
            "relay-method", long.MaxValue, ContactStatus.Pending, Now, Now.AddHours(1));
        var relation = new ContactRelation(summary, false, Now);

        var result = relation.SetBlocked(true, long.MaxValue, Now);

        result.Outcome.ShouldBe(ContactRelationOutcome.GenerationExhausted);
        result.Relation.ShouldBe(relation);
        result.Relation.CanDeliver.ShouldBeFalse();
    }

    [Fact]
    public void Block_AlreadyUnblockedAcceptedRelation_PreservesCurrentContact()
    {
        var summary = new ContactRequestSummary(new ContactRequestId("request-accepted"), new ContactCardId("public-card"),
            new MailboxId("requester"), new MailboxId("recipient"), "relay-method", 1,
            ContactStatus.Accepted, Now, Now.AddHours(1));
        var relation = new ContactRelation(summary, false, Now);

        var result = relation.SetBlocked(false, 1, Now.AddMinutes(1));

        result.Outcome.ShouldBe(ContactRelationOutcome.Applied);
        result.Relation.CanDeliver.ShouldBeTrue();
        result.Relation.Generation.ShouldBe(1);
    }

    [Fact]
    public void CanDeliver_ConnectedFlagWithoutAcceptedRequest_DeniesDelivery()
    {
        var summary = new ContactRequestSummary(new ContactRequestId("request-pending"), new ContactCardId("public-card"),
            new MailboxId("requester"), new MailboxId("recipient"), "relay-method", 1,
            ContactStatus.Pending, Now, Now.AddHours(1));
        var relation = new ContactRelation(summary, false, Now) { IsConnected = true };

        relation.CanDeliver.ShouldBeFalse();
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
    public void Validate_UntrustedCardBounds_ReturnsInvalid(int invalidCase)
    {
        var card = Card();
        card = invalidCase switch
        {
            0 => card with { CardId = new ContactCardId(new string('c', 129)) },
            1 => card with { OwnerMailboxId = new MailboxId(new string('m', 129)) },
            2 => card with { AudienceHint = new string('界', 342) },
            3 => card with { Methods = Enumerable.Range(0, 17).Select(index => Method() with { MethodId = $"method-{index}" }).ToImmutableArray() },
            4 => card with { Methods = [Method() with { Endpoint = " " }] },
            5 => card with { Methods = [Method() with { Endpoint = new string('界', 683) }] },
            6 => card with { Methods = [Method() with { MethodId = " " }] },
            7 => card with { Methods = [Method() with { Instructions = new string('界', 21846) }] },
            8 => card with { RevokedAt = card.CreatedAt.AddTicks(-1) },
            9 => card with { RevokedAt = Now.AddTicks(1) },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidCase))
        };

        var validity = ContactCardPolicy.Validate(card, Now);

        validity.ShouldBe(ContactCardValidation.Invalid);
    }

    [Fact]
    public void Validate_AtAllDeclaredBounds_AcceptsCard()
    {
        var card = Card() with
        {
            CardId = new ContactCardId(new string('c', 128)),
            OwnerMailboxId = new MailboxId(new string('m', 128)),
            AudienceHint = new string('h', 1024),
            Methods = Enumerable.Range(0, 16).Select(index => Method() with
            {
                MethodId = $"{index:D3}" + new string('m', 125),
                Endpoint = new string('e', 2048),
                Instructions = new string('i', 65536)
            }).ToImmutableArray()
        };

        var validity = ContactCardPolicy.Validate(card, Now);

        validity.ShouldBe(ContactCardValidation.Valid);
    }

    [Theory]
    [InlineData("relay")]
    [InlineData("direct")]
    public void ChooseMethod_SupportedTransport_PreservesSelectedDescription(string transport)
    {
        var card = Card() with { Methods = [Method() with { Transport = transport }] };

        var result = ContactCardPolicy.ChooseMethod(card, "offered-method");

        result.Outcome.ShouldBe(ContactMethodChoiceOutcome.Selected);
        result.Method.ShouldNotBeNull();
        result.Method.Transport.ShouldBe(transport);
    }

    [Fact]
    public void ChooseMethod_DuplicateMethodId_RejectsAmbiguousSelection()
    {
        var card = Card() with { Methods = [Method(), Method() with { Endpoint = "other-recipient" }] };

        var result = ContactCardPolicy.ChooseMethod(card, "offered-method");

        result.Outcome.ShouldBe(ContactMethodChoiceOutcome.Unsupported);
        result.Method.ShouldBeNull();
    }

    [Fact]
    public void ChooseMethod_UnsupportedTransport_RejectsSelection()
    {
        var card = Card() with { Methods = [Method() with { Transport = "arbitrary-transport" }] };

        var result = ContactCardPolicy.ChooseMethod(card, "offered-method");

        result.Outcome.ShouldBe(ContactMethodChoiceOutcome.Unsupported);
        result.Method.ShouldBeNull();
    }

    [Fact]
    public void ChooseMethod_DefaultMethods_ReturnsUnsupported()
    {
        var card = Card() with { Methods = default };

        var result = ContactCardPolicy.ChooseMethod(card, "offered-method");

        result.Outcome.ShouldBe(ContactMethodChoiceOutcome.Unsupported);
        result.Method.ShouldBeNull();
    }

    [Fact]
    public void Discover_UnlistedCardWithAudienceHint_DoesNotEnforceGroupMembership()
    {
        var card = Card() with { Visibility = ContactVisibility.Unlisted, AudienceHint = "team-alpha" };

        var discoverable = ContactCardPolicy.CanDiscover(card, new MailboxId("team-alpha"));

        discoverable.ShouldBeFalse();
        ContactCardPolicy.CanDiscover(card, card.OwnerMailboxId).ShouldBeTrue();
    }

    [Fact]
    public void Discover_DefaultVisibility_DoesNotExposeCard()
    {
        var card = Card() with { Visibility = default };

        var discoverable = ContactCardPolicy.CanDiscover(card, new MailboxId("unrelated-agent"));

        discoverable.ShouldBeFalse();
    }

    [Fact]
    public void Discover_EmptyOwnerAndCaller_DoNotExposeUnlistedCard()
    {
        var card = Card() with { OwnerMailboxId = MailboxId.Empty, Visibility = ContactVisibility.Unlisted };

        var discoverable = ContactCardPolicy.CanDiscover(card, MailboxId.Empty);

        discoverable.ShouldBeFalse();
    }

    [Fact]
    public void Discover_ExpiredRevokedPublicCard_StillUsesVisibilityOnly()
    {
        var card = Card() with { ExpiresAt = Now, RevokedAt = Now };

        var discoverable = ContactCardPolicy.CanDiscover(card, new MailboxId("requester"));

        discoverable.ShouldBeTrue();
        ContactCardPolicy.Validate(card, Now).ShouldBe(ContactCardValidation.Revoked);
    }

    private static ContactCard Card() => new(new ContactCardId("public-card"), new MailboxId("recipient"),
        ContactVisibility.Public, Now.AddMinutes(-1), Now.AddHours(1), [Method()]);

    private static ContactMethod Method() => new("offered-method", 1, "relay", "recipient-relay", "opaque challenge instructions");
}
