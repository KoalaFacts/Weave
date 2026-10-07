using Weave.Contacts;
using Weave.Mailboxes;

namespace Weave.Mailbox.Host;

internal static class MailboxWireMapping
{
    internal static MailboxPayload Payload(PayloadWire wire) => new(wire.MessageId, wire.CreatedAt,
        wire.ExpiresAt, wire.PayloadEncoding, wire.Bytes);
    internal static PayloadWire Payload(MailboxPayload payload) => new(payload.MessageId, payload.CreatedAt,
        payload.ExpiresAt, payload.PayloadEncoding, payload.Bytes.ToArray());
    internal static ReceiptWire Receipt(MailboxReceipt receipt) => new(receipt.MessageId,
        receipt.SenderMailboxId.Value, receipt.RecipientMailboxId.Value, receipt.State switch
        {
            MailboxReceiptState.Pending => "pending",
            MailboxReceiptState.Acknowledged => "acknowledged",
            MailboxReceiptState.Expired => "expired",
            MailboxReceiptState.Blocked => "blocked",
            _ => throw new ArgumentOutOfRangeException(nameof(receipt))
        }, receipt.ExpiresAt, receipt.TerminalAt);
    internal static ReceivedMessageWire Message(ReceivedMailboxMessage message) => new(message.SenderMailboxId.Value,
        new(message.Envelope.Version, message.Envelope.RecipientMailboxId.Value, message.Envelope.ContactGeneration,
            Payload(message.Envelope.Payload)));
    internal static CardWire Card(ContactCard card) => new(card.CardId.Value, card.OwnerMailboxId.Value,
        card.Visibility == ContactVisibility.Public ? "public" : "unlisted", card.CreatedAt, card.ExpiresAt,
        card.RevokedAt, card.AudienceHint, card.Methods.Select(m => new ContactMethodWire(m.MethodId, m.Version,
            m.Transport, m.Endpoint, m.Instructions)).ToArray());
    internal static ContactSummaryWire Summary(ContactRequestSummary request) => new(request.RequestId.Value,
        request.CardId.Value, request.RequesterMailboxId.Value, request.RecipientMailboxId.Value, request.MethodId,
        request.Generation, Status(request.Status), request.CreatedAt, request.ExpiresAt, request.RequestMessageId, request.ReplyMessageId);
    internal static ContactRelationWire Relation(ContactRelation relation) => new(Summary(relation.Request),
        relation.Generation, relation.IsBlocked, relation.IsConnected, relation.UpdatedAt);
    internal static ContactChannelWire Channel(ContactChannelSnapshot channel) => new(channel.MailboxId.Value,
        channel.PeerMailboxId.Value, channel.Generation, channel.IsBlockedByMailbox, channel.IsBlockedByPeer, channel.CurrentRequest is { } request ? new(request.RequesterMailboxId.Value, request.RequestId.Value) : null);
    internal static string Status(ContactStatus status) => status switch
    {
        ContactStatus.Pending => "pending",
        ContactStatus.NeedsAction => "needsAction",
        ContactStatus.Accepted => "accepted",
        ContactStatus.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
    internal static ContactStatus? Status(string status) => status switch
    {
        "pending" => ContactStatus.Pending,
        "needsAction" => ContactStatus.NeedsAction,
        "accepted" => ContactStatus.Accepted,
        "rejected" => ContactStatus.Rejected,
        _ => null
    };
}
