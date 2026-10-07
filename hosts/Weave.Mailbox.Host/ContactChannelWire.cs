namespace Weave.Mailbox.Host;

public sealed record ContactChannelWire(string MailboxId, string PeerMailboxId, long Generation, bool IsBlockedByMailbox, bool IsBlockedByPeer, ContactRequestLocatorWire? CurrentRequest);
