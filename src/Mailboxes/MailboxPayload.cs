using System.Collections.Immutable;

namespace Weave.Mailboxes;

public sealed record MailboxPayload
{
    public const int MaximumBytes = 65536;

    public MailboxPayload(Guid messageId, DateTimeOffset createdAt, DateTimeOffset expiresAt,
        string payloadEncoding, ReadOnlySpan<byte> bytes)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes.Length, MaximumBytes, nameof(bytes));
        MessageId = messageId;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        PayloadEncoding = payloadEncoding;
        Bytes = ImmutableArray.CreateRange(bytes.ToArray());
    }

    public Guid MessageId { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public string PayloadEncoding { get; }
    public ImmutableArray<byte> Bytes { get; }
}
