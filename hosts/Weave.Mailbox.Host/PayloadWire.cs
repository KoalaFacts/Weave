namespace Weave.Mailbox.Host;

public sealed record PayloadWire(Guid MessageId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string PayloadEncoding, byte[] Bytes);
