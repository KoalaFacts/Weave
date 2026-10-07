using Weave.Mailboxes;

namespace Weave.Contacts;

public sealed record ContactRequestLocator(MailboxId RequesterMailboxId, ContactRequestId RequestId);
