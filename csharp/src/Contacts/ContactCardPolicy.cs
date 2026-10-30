using System.Text;
using Weave.Mailboxes;

namespace Weave.Contacts;

public static class ContactCardPolicy
{
    public const int MaximumMethods = 16;
    public const int MaximumIdentifierLength = 128;
    public const int MaximumEndpointBytes = 2048;
    public const int MaximumAudienceHintBytes = 1024;
    public const int MaximumInstructionsBytes = 65536;

    public static ContactCardValidation Validate(ContactCard card, DateTimeOffset now)
    {
        if (!ValidIdentifier(card.CardId.Value) || !ValidIdentifier(card.OwnerMailboxId.Value)
            || !Enum.IsDefined(card.Visibility) || card.CreatedAt > now
            || card.ExpiresAt is { } expiry && expiry <= card.CreatedAt
            || card.RevokedAt is { } revokedAt && (revokedAt < card.CreatedAt || revokedAt > now)
            || card.AudienceHint is { } hint && Encoding.UTF8.GetByteCount(hint) > MaximumAudienceHintBytes
            || card.Methods.IsDefaultOrEmpty || card.Methods.Length > MaximumMethods)
            return ContactCardValidation.Invalid;

        var methodIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in card.Methods)
        {
            if (!ValidMethod(method) || !methodIds.Add(method.MethodId))
                return ContactCardValidation.Invalid;
        }

        if (card.RevokedAt is not null)
            return ContactCardValidation.Revoked;
        return card.ExpiresAt is { } expiresAt && now >= expiresAt
            ? ContactCardValidation.Expired
            : ContactCardValidation.Valid;
    }

    public static bool CanDiscover(ContactCard card, MailboxId callerMailboxId) =>
        card.Visibility == ContactVisibility.Public
        || card.Visibility == ContactVisibility.Unlisted
            && !card.OwnerMailboxId.IsEmpty && card.OwnerMailboxId == callerMailboxId;

    public static ContactMethodChoice ChooseMethod(ContactCard card, string methodId)
    {
        if (card.Methods.IsDefaultOrEmpty)
            return new(ContactMethodChoiceOutcome.Unsupported);

        ContactMethod? selected = null;
        foreach (var method in card.Methods)
        {
            if (!string.Equals(method.MethodId, methodId, StringComparison.Ordinal))
                continue;
            if (selected is not null || !ValidMethod(method))
                return new(ContactMethodChoiceOutcome.Unsupported);
            selected = method;
        }

        return selected is null
            ? new(ContactMethodChoiceOutcome.Unsupported)
            : new(ContactMethodChoiceOutcome.Selected, selected);
    }

    private static bool ValidIdentifier(string? identifier) =>
        !string.IsNullOrWhiteSpace(identifier) && identifier.Length <= MaximumIdentifierLength;

    private static bool ValidMethod(ContactMethod method) =>
        ValidIdentifier(method.MethodId) && method.Version > 0 && method.Transport is "relay" or "direct"
        && !string.IsNullOrWhiteSpace(method.Endpoint)
        && Encoding.UTF8.GetByteCount(method.Endpoint) <= MaximumEndpointBytes
        && Encoding.UTF8.GetByteCount(method.Instructions) <= MaximumInstructionsBytes;
}
