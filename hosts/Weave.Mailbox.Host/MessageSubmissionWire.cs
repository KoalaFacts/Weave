namespace Weave.Mailbox.Host;

public sealed record MessageSubmissionWire(int Version, string RecipientMailboxId, long ContactGeneration, PayloadWire Payload);
