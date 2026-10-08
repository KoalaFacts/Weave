namespace Weave.Mailbox.Host;

public sealed record ReceiptWire(Guid MessageId, string SenderMailboxId, string RecipientMailboxId, string State, DateTimeOffset ExpiresAt, DateTimeOffset? TerminalAt);
