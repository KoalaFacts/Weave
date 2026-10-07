namespace Weave.Mailboxes;

public sealed record MailboxReceipt(Guid MessageId, MailboxId SenderMailboxId, MailboxId RecipientMailboxId,
    MailboxReceiptState State, DateTimeOffset ExpiresAt, DateTimeOffset? TerminalAt);
