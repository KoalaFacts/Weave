namespace Weave.Mailbox.Host;

public sealed record MailboxProblemWire(int Status, string Title, string Code);
