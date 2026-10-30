namespace Weave.Mailbox.Host;

public sealed record ContactSubmissionWire(string RequestId, string CardId, string MethodId, PayloadWire Payload);
