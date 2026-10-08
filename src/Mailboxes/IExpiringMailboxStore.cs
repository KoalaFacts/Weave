using Weave.Contacts;

namespace Weave.Mailboxes;

public interface IExpiringMailboxStore
{
    MailboxResult<ContactCard> PutCard(MailboxAuthority authority, ContactCard card, CancellationToken ct);
    MailboxPage<ContactCard> ListCards(MailboxAuthority? owner, string? afterCursor, int limit, CancellationToken ct);
    ContactCard? FindCard(ContactCardId cardId, MailboxAuthority? viewer, CancellationToken ct);
    MailboxResult<ContactRelation> RequestContact(MailboxAuthority authority, ContactRequestSubmission request, CancellationToken ct);
    MailboxPage<ContactRequestSummary> ListContactRequests(MailboxAuthority authority, string? afterCursor, int limit, CancellationToken ct);
    ContactRequestSummary? GetContactRequest(MailboxAuthority authority, ContactRequestLocator request, CancellationToken ct);
    MailboxResult<ContactRelation> DecideContact(MailboxAuthority authority, ContactDecision decision, CancellationToken ct);
    ContactChannelSnapshot? GetContactChannel(MailboxAuthority authority, MailboxId peerMailboxId, CancellationToken ct);
    MailboxResult<ContactChannelSnapshot> SetBlocked(MailboxAuthority authority, MailboxId peerMailboxId,
        long expectedGeneration, bool blocked, CancellationToken ct);
    MailboxResult<MailboxReceipt> Send(MailboxAuthority authority, MailboxEnvelope envelope, CancellationToken ct);
    MailboxPage<ReceivedMailboxMessage> ReadPending(MailboxAuthority authority, string? afterCursor, int limit, CancellationToken ct);
    MailboxReceipt? GetReceipt(MailboxAuthority authority, Guid messageId, CancellationToken ct);
    MailboxPage<MailboxReceipt> ListReceipts(MailboxAuthority authority, string? afterCursor, int limit, CancellationToken ct);
    MailboxResult<MailboxReceipt> Acknowledge(MailboxAuthority authority, MailboxId senderMailboxId, Guid messageId, CancellationToken ct);
    int Sweep(int batchSize, CancellationToken ct);
}
