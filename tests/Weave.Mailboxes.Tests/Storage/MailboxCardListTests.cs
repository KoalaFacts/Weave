using Weave.Contacts;
using static Weave.Mailboxes.Tests.Storage.MailboxTestContext;

namespace Weave.Mailboxes.Tests.Storage;

public sealed class MailboxCardListTests
{
    [Fact]
    public void ListCards_VisibilityLifetimeAndScopedCursor_OnlyEligibleOwnedOrPublic()
    {
        using var context = new MailboxTestContext();
        foreach (var card in new[] { context.Card("a"), context.Card("b", ContactVisibility.Unlisted),
            context.Card("c", owner: Alice), context.Card("d") with { ExpiresAt = context.Clock.GetUtcNow().AddSeconds(1) },
            context.Card("e") with { RevokedAt = context.Clock.GetUtcNow() } })
            Require(context.Store.PutCard(new(card.OwnerMailboxId), card, Ct));
        context.Clock.Advance(TimeSpan.FromSeconds(1));
        var first = context.Store.ListCards(null, null, 1, Ct);
        first.Items.Single().CardId.Value.ShouldBe("a");
        first.NextCursor.ShouldNotBeNull();
        context.Store.ListCards(null, first.NextCursor, 1, Ct).Items.Single().CardId.Value.ShouldBe("c");
        var own = context.Store.ListCards(Bob, null, 1, Ct);
        own.Items.Single().CardId.Value.ShouldBe("a");
        own.NextCursor.ShouldNotBeNull();
        context.Store.ListCards(Bob, own.NextCursor, 10, Ct).Items.Single().CardId.Value.ShouldBe("b");
        context.Store.ListCards(Alice, own.NextCursor, 10, Ct).Items.ShouldBeEmpty();
        context.Store.ListCards(null, own.NextCursor, 10, Ct).Items.ShouldBeEmpty();
        context.Store.ListCards(Bob, first.NextCursor, 10, Ct).Items.ShouldBeEmpty();
    }
    [Theory]
    [InlineData(0, null)]
    [InlineData(101, null)]
    [InlineData(1, "invalid")]
    public void ListCards_InvalidPage_Empty(int limit, string? cursor)
    {
        using var context = new MailboxTestContext();
        Require(context.Store.PutCard(Bob, context.Card(), Ct));
        context.Store.ListCards(null, cursor, limit, Ct).Items.ShouldBeEmpty();
    }
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
