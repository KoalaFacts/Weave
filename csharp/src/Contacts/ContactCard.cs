using System.Collections.Immutable;
using Weave.Mailboxes;

namespace Weave.Contacts;

public sealed record ContactCard
{
    public ContactCard(ContactCardId cardId, MailboxId ownerMailboxId, ContactVisibility visibility,
        DateTimeOffset createdAt, DateTimeOffset? expiresAt, IEnumerable<ContactMethod> methods)
    {
        CardId = cardId;
        OwnerMailboxId = ownerMailboxId;
        Visibility = visibility;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        Methods = [.. methods];
    }

    public ContactCardId CardId { get; init; }
    public MailboxId OwnerMailboxId { get; init; }
    public ContactVisibility Visibility { get; init; }
    public string? AudienceHint { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; init; }
    public ImmutableArray<ContactMethod> Methods { get; init; }
}
