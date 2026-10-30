using Weave.Mailboxes;

namespace Weave.Contacts;

public sealed record ContactChannelSnapshot(MailboxId MailboxId, MailboxId PeerMailboxId, long Generation,
    bool IsBlockedByMailbox, bool IsBlockedByPeer, ContactRequestLocator? CurrentRequest)
{
    public bool IsBlocked => IsBlockedByMailbox || IsBlockedByPeer;
}
