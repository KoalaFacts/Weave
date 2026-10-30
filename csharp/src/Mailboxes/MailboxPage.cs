using System.Collections.Immutable;

namespace Weave.Mailboxes;

public sealed record MailboxPage<T>(ImmutableArray<T> Items, string? NextCursor);
