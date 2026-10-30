using Weave.Mailboxes;

namespace Weave.Contacts;

public sealed record ContactRequest(ContactRequestId RequestId, ContactCardId CardId, MailboxId RequesterMailboxId,
    MailboxId RecipientMailboxId, string MethodId, long Generation, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    MailboxPayload Payload)
{
    public ContactRequestSummary ToSummary() => new(RequestId, CardId, RequesterMailboxId, RecipientMailboxId,
        MethodId, Generation, ContactStatus.Pending, CreatedAt, ExpiresAt, Payload.MessageId);
}
