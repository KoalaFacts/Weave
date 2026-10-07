using Weave.Mailboxes;

namespace Weave.Contacts;

public sealed record ContactRequestSummary(ContactRequestId RequestId, ContactCardId CardId,
    MailboxId RequesterMailboxId, MailboxId RecipientMailboxId, string MethodId, long Generation, ContactStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, Guid? RequestMessageId = null, Guid? ReplyMessageId = null)
{
    public ContactRequestLocator Locator => new(RequesterMailboxId, RequestId);
}
