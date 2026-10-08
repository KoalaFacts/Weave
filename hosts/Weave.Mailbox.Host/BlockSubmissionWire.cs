namespace Weave.Mailbox.Host;

public sealed record BlockSubmissionWire(string PeerMailboxId, long ExpectedGeneration);
