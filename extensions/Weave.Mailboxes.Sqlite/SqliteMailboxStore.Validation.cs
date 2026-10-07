using System.Security.Cryptography;
using System.Text;
using Weave.Contacts;

namespace Weave.Mailboxes.Sqlite;

public sealed partial class SqliteMailboxStore
{
    private MailboxError? ValidatePayload(MailboxPayload payload, DateTimeOffset now, bool firstAdmission)
    {
        if (!TimestampMatches(payload.MessageId, payload.CreatedAt) || payload.ExpiresAt <= payload.CreatedAt
            || payload.ExpiresAt - payload.CreatedAt > _options.MaximumMessageLifetime
            || string.IsNullOrWhiteSpace(payload.PayloadEncoding) || payload.PayloadEncoding.Length > 128
            || payload.PayloadEncoding.Any(char.IsControl) || payload.Bytes.IsDefault
            || payload.Bytes.Length > MailboxPayload.MaximumBytes || payload.CreatedAt - now > _options.FutureClockSkew)
            return MailboxError.Invalid;
        if (firstAdmission && (payload.ExpiresAt <= now || now - payload.CreatedAt > _options.FirstAdmissionWindow))
            return MailboxError.Expired;
        return null;
    }

    private static bool TimestampMatches(Guid id, DateTimeOffset created)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        if ((bytes[6] >> 4) != 7 || (bytes[8] & 0xc0) != 0x80) return false;
        long milliseconds = 0;
        for (var i = 0; i < 6; i++) milliseconds = (milliseconds << 8) | bytes[i];
        return milliseconds == created.ToUnixTimeMilliseconds();
    }

    private static string Fingerprint(Action<BinaryWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true)) write(writer);
        return Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
    }

    private static void WritePayload(BinaryWriter writer, MailboxPayload payload)
    {
        writer.Write(payload.MessageId.ToByteArray());
        writer.Write(payload.CreatedAt.UtcTicks);
        writer.Write(payload.ExpiresAt.UtcTicks);
        writer.Write(payload.PayloadEncoding);
        writer.Write(payload.Bytes.Length);
        writer.Write(payload.Bytes.AsSpan());
    }

    // Persisted hashes stay byte-compatible; scoped row keys and reply recipients bind requester identity.
    private static string MessageFingerprint(MailboxEnvelope envelope, int purpose, string? requestId) => Fingerprint(writer =>
    {
        writer.Write(envelope.Version);
        writer.Write(envelope.RecipientMailboxId.Value);
        writer.Write(envelope.ContactGeneration);
        writer.Write(purpose);
        writer.Write(requestId ?? "");
        WritePayload(writer, envelope.Payload);
    });

    private static string RequestFingerprint(ContactRequestSubmission request) => Fingerprint(writer =>
    {
        writer.Write(request.CardId.Value);
        writer.Write(request.MethodId);
        WritePayload(writer, request.Payload);
    });

    private static string DecisionFingerprint(ContactDecision decision) => Fingerprint(writer =>
    {
        writer.Write(decision.Request.RequestId.Value);
        writer.Write(decision.ExpectedGeneration);
        writer.Write((int)decision.Status);
        writer.Write(decision.Reply is not null);
        if (decision.Reply is { } reply) WritePayload(writer, reply);
    });
}
