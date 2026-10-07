namespace Weave.Mailboxes;

public sealed record MailboxEnvelope(int Version, MailboxId RecipientMailboxId, long ContactGeneration,
    MailboxPayload Payload)
{
    public Guid MessageId => Payload.MessageId;
    public DateTimeOffset CreatedAt => Payload.CreatedAt;
    public DateTimeOffset ExpiresAt => Payload.ExpiresAt;
    public string PayloadEncoding => Payload.PayloadEncoding;
}
