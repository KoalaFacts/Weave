using Weave.Contacts;

namespace Weave.Mailboxes.Sqlite;

internal sealed record MailboxRequestRow(long Sequence, ContactRequestSummary Summary, string Fingerprint,
    string? DecisionFingerprint, DateTimeOffset UpdatedAt, DateTimeOffset? TerminalAt);
