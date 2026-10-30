namespace Weave.Mailbox.Host;

public sealed record ContactDecisionWire(string RequesterMailboxId, long ExpectedGeneration, string Status, PayloadWire? Reply);
