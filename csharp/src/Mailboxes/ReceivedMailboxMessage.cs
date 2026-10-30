namespace Weave.Mailboxes;

public sealed record ReceivedMailboxMessage(MailboxId SenderMailboxId, MailboxEnvelope Envelope);
