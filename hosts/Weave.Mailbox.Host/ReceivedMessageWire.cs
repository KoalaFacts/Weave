namespace Weave.Mailbox.Host;

public sealed record ReceivedMessageWire(string SenderMailboxId, MessageSubmissionWire Envelope);
