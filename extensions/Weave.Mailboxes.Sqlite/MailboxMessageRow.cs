namespace Weave.Mailboxes.Sqlite;

internal sealed record MailboxMessageRow(long Sequence, MailboxReceipt Receipt, long Generation,
    string Fingerprint);
