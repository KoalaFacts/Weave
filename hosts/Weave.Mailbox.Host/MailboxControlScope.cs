using Weave.Mailboxes;

namespace Weave.Mailbox.Host;

internal sealed record MailboxControlScope(MailboxAuthority Authority, string Sha256);
