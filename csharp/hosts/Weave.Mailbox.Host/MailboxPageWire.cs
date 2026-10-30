namespace Weave.Mailbox.Host;

public sealed record MailboxPageWire<T>(T[] Items, string? NextCursor);
