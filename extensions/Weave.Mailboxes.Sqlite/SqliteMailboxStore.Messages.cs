using Weave.Contacts;

namespace Weave.Mailboxes.Sqlite;

public sealed partial class SqliteMailboxStore
{
    public MailboxResult<MailboxReceipt> Send(MailboxAuthority authority, MailboxEnvelope envelope, CancellationToken ct)
    {
        using var db = Session(true, ct);
        var now = _timeProvider.GetUtcNow();
        if (!ValidId(authority.MailboxId.Value))
            return new(MailboxError.Forbidden);
        if (!ValidId(envelope.RecipientMailboxId.Value) || envelope.RecipientMailboxId == authority.MailboxId
            || envelope.Version != 1)
            return new(MailboxError.Invalid);
        var validation = ValidatePayload(envelope.Payload, now, firstAdmission: false);
        if (validation is not null)
            return new(validation.Value);
        var existing = ReadMessage(db, authority.MailboxId, envelope.MessageId);
        if (existing is not null)
        {
            if (existing.Fingerprint != MessageFingerprint(envelope, 0, null))
                return new(MailboxError.Conflict);
            var receipt = TerminalizeEffective(db, existing, now);
            db.Commit();
            return new(receipt);
        }
        validation = ValidatePayload(envelope.Payload, now, firstAdmission: true);
        if (validation is not null)
            return new(validation.Value);
        var channel = ReadChannel(db, authority.MailboxId.Value, envelope.RecipientMailboxId.Value);
        if (channel is null || channel.IsBlocked)
            return new(MailboxError.Forbidden);
        if (channel.Generation != envelope.ContactGeneration)
            return new(MailboxError.Conflict);
        var request = channel.CurrentRequest is null ? null : ReadRequest(db, new(new(channel.CurrentRequester!), new(channel.CurrentRequest)));
        if (request?.Summary.Status != ContactStatus.Accepted || request.Summary.Generation != channel.Generation)
            return new(MailboxError.Forbidden);
        var result = AdmitMessage(db, authority.MailboxId, envelope, channel, 0, null, now);
        if (result.IsSuccess)
            db.Commit();
        return result;
    }

    private MailboxResult<MailboxReceipt> AdmitMessage(MailboxDatabaseSession db, MailboxId sender,
        MailboxEnvelope envelope, MailboxChannelRow channel, int purpose, ContactRequestLocator? request, DateTimeOffset now)
    {
        var fingerprint = MessageFingerprint(envelope, purpose, request?.RequestId.Value);
        var existing = ReadMessage(db, sender, envelope.MessageId);
        if (existing is not null)
            return existing.Fingerprint == fingerprint ? new(TerminalizeEffective(db, existing, now)) : new(MailboxError.Conflict);
        db.Execute("""
            UPDATE mailbox_messages SET state=2,terminal=expires,payload=NULL,payload_size=0
            WHERE state=0 AND expires<=$now
            """, ("$now", now.UtcTicks));
        if (db.Scalar("SELECT count(*) FROM mailbox_messages") >= _options.MaximumMessageRows
            || db.Scalar("SELECT count(*) FROM mailbox_messages WHERE recipient=$recipient AND state=0 AND expires>$now",
                ("$recipient", envelope.RecipientMailboxId.Value), ("$now", now.UtcTicks)) >= _options.MaximumPendingMessagesPerMailbox
            || db.Scalar("SELECT COALESCE(sum(payload_size),0) FROM mailbox_messages WHERE state=0 AND expires>$now",
                ("$now", now.UtcTicks)) > _options.MaximumPendingBytes - envelope.Payload.Bytes.Length)
            return new(MailboxError.Capacity);
        db.Execute("""
            INSERT INTO mailbox_messages(sender,id,recipient,low,high,generation,version,created,expires,
                encoding,payload,payload_size,fingerprint,purpose,request_id,request_requester,state)
            VALUES($sender,$id,$recipient,$low,$high,$generation,$version,$created,$expires,
                $encoding,$payload,$size,$fingerprint,$purpose,$request,$requester,0)
            """, ("$sender", sender.Value), ("$id", envelope.MessageId.ToString()), ("$recipient", envelope.RecipientMailboxId.Value),
            ("$low", channel.Low), ("$high", channel.High), ("$generation", envelope.ContactGeneration), ("$version", envelope.Version),
            ("$created", envelope.CreatedAt.UtcTicks), ("$expires", envelope.ExpiresAt.UtcTicks),
            ("$encoding", envelope.PayloadEncoding), ("$payload", envelope.Payload.Bytes.ToArray()), ("$size", envelope.Payload.Bytes.Length),
            ("$fingerprint", fingerprint), ("$purpose", purpose), ("$request", request?.RequestId.Value), ("$requester", request?.RequesterMailboxId.Value));
        return new(new MailboxReceipt(envelope.MessageId, sender, envelope.RecipientMailboxId, MailboxReceiptState.Pending, envelope.ExpiresAt, null));
    }

    public MailboxResult<MailboxReceipt> Acknowledge(MailboxAuthority authority, MailboxId senderMailboxId,
        Guid messageId, CancellationToken ct)
    {
        using var db = Session(true, ct);
        var now = _timeProvider.GetUtcNow();
        var message = ReadMessage(db, senderMailboxId, messageId);
        if (message is null)
            return new(MailboxError.Unavailable);
        if (message.Receipt.RecipientMailboxId != authority.MailboxId)
            return new(MailboxError.Forbidden);
        var receipt = TerminalizeEffective(db, message, now);
        if (receipt.State == MailboxReceiptState.Pending)
        {
            receipt = receipt with { State = MailboxReceiptState.Acknowledged, TerminalAt = now };
            Terminalize(db, message.Sequence, receipt);
        }
        db.Commit();
        return new(receipt);
    }

    private static void Terminalize(MailboxDatabaseSession db, long sequence, MailboxReceipt receipt) => db.Execute(
        "UPDATE mailbox_messages SET state=$state,terminal=$terminal,payload=NULL,payload_size=0 WHERE seq=$seq AND state=0",
        ("$state", (int)receipt.State), ("$terminal", receipt.TerminalAt!.Value.UtcTicks), ("$seq", sequence));

    private static MailboxReceipt TerminalizeEffective(MailboxDatabaseSession db, MailboxMessageRow message, DateTimeOffset now)
    {
        var receipt = EffectiveReceipt(db, message, now);
        if (message.Receipt.State == MailboxReceiptState.Pending && receipt.State != MailboxReceiptState.Pending)
            Terminalize(db, message.Sequence, receipt);
        return receipt;
    }

    private static MailboxReceipt EffectiveReceipt(MailboxDatabaseSession db, MailboxMessageRow message, DateTimeOffset now)
    {
        var receipt = message.Receipt;
        if (receipt.State != MailboxReceiptState.Pending)
            return receipt;
        if (receipt.ExpiresAt <= now)
            return receipt with { State = MailboxReceiptState.Expired, TerminalAt = receipt.ExpiresAt };
        var channel = ReadChannel(db, receipt.SenderMailboxId.Value, receipt.RecipientMailboxId.Value)!;
        if (channel.IsBlocked || channel.Generation != message.Generation)
            return receipt with { State = MailboxReceiptState.Blocked, TerminalAt = channel.UpdatedAt };
        return receipt;
    }
}
