using Weave.Contacts;

namespace Weave.Mailboxes.Sqlite;

public sealed partial class SqliteMailboxStore
{
    public MailboxResult<ContactRelation> RequestContact(MailboxAuthority authority, ContactRequestSubmission request, CancellationToken ct)
    {
        using var db = Session(true, ct);
        var now = _timeProvider.GetUtcNow();
        if (!ValidId(authority.MailboxId.Value))
            return new(MailboxError.Forbidden);
        if (!ValidId(request.CardId.Value) || !ValidId(request.MethodId)
            || !Guid.TryParse(request.RequestId.Value, out var requestId)
            || request.RequestId.Value != requestId.ToString()
            || !TimestampMatches(requestId, request.Payload.CreatedAt))
            return new(MailboxError.Invalid);
        var validation = ValidatePayload(request.Payload, now, firstAdmission: false);
        if (validation is not null)
            return new(validation.Value);
        var fingerprint = RequestFingerprint(request);
        var previous = ReadRequest(db, new(authority.MailboxId, request.RequestId));
        if (previous is not null)
        {
            if (previous.Fingerprint != fingerprint)
                return new(MailboxError.Conflict);
            var previousChannel = ReadChannel(db, authority.MailboxId.Value, previous.Summary.RecipientMailboxId.Value)!;
            return new(previousChannel.Relation(previous.Summary, previous.UpdatedAt));
        }
        validation = ValidatePayload(request.Payload, now, firstAdmission: true);
        if (validation is not null)
            return new(validation.Value);
        var card = ReadCard(db, request.CardId);
        if (card is null || ContactCardPolicy.Validate(card, now) != ContactCardValidation.Valid)
            return new(MailboxError.Unavailable);
        if (card.OwnerMailboxId == authority.MailboxId)
            return new(MailboxError.Invalid);
        var method = ContactCardPolicy.ChooseMethod(card, request.MethodId);
        if (method.Method?.Transport != "relay")
            return new(MailboxError.Invalid);
        var recipient = card.OwnerMailboxId;
        var channel = ReadChannel(db, authority.MailboxId.Value, recipient.Value);
        if (channel?.IsBlocked == true)
            return new(MailboxError.Forbidden);
        if (channel?.Generation == long.MaxValue)
            return new(MailboxError.Capacity);
        if (channel?.CurrentRequest is { } currentId)
        {
            var current = ReadRequest(db, new(new(channel.CurrentRequester!), new(currentId)))!;
            if (current.TerminalAt is null && (current.Summary.Status == ContactStatus.Accepted || current.Summary.ExpiresAt > now))
                return new(MailboxError.Conflict);
        }
        if (db.Scalar("SELECT count(*) FROM contact_requests") >= _options.MaximumRequestRows
            || db.Scalar("""
                SELECT count(*) FROM contact_requests WHERE recipient=$recipient AND status IN (0,1)
                    AND expires>$now AND terminal IS NULL
                """, ("$recipient", recipient.Value), ("$now", now.UtcTicks)) >= _options.MaximumPendingRequestsPerRecipient
            || channel is null && db.Scalar("SELECT count(*) FROM contact_channels") >= _options.MaximumChannels
            || !EnsureOwner(db, authority.MailboxId.Value))
            return new(MailboxError.Capacity);
        var (low, high) = Pair(authority.MailboxId.Value, recipient.Value);
        if (channel is not null)
            InvalidateChannel(db, channel, now);
        channel = new(low, high, (channel?.Generation ?? 0) + 1, false, false, request.RequestId.Value, authority.MailboxId.Value, now);
        WriteChannel(db, channel);
        var envelope = new MailboxEnvelope(1, recipient, channel.Generation, request.Payload);
        var admission = AdmitMessage(db, authority.MailboxId, envelope, channel, 1, new(authority.MailboxId, request.RequestId), now);
        if (!admission.IsSuccess)
            return new(admission.Error!.Value);
        var summary = new ContactRequestSummary(request.RequestId, request.CardId, authority.MailboxId, recipient,
            request.MethodId, channel.Generation, ContactStatus.Pending, request.Payload.CreatedAt,
            request.Payload.ExpiresAt, request.Payload.MessageId);
        db.Execute("""
            INSERT INTO contact_requests(id,card,requester,recipient,method,generation,status,created,expires,
                request_message,fingerprint,updated) VALUES($id,$card,$requester,$recipient,$method,$generation,0,
                $created,$expires,$message,$fingerprint,$updated)
            """, ("$id", request.RequestId.Value), ("$card", request.CardId.Value), ("$requester", authority.MailboxId.Value),
            ("$recipient", recipient.Value), ("$method", request.MethodId), ("$generation", channel.Generation),
            ("$created", summary.CreatedAt.UtcTicks), ("$expires", summary.ExpiresAt.UtcTicks),
            ("$message", request.Payload.MessageId.ToString()), ("$fingerprint", fingerprint), ("$updated", now.UtcTicks));
        db.Commit();
        return new(channel.Relation(summary, now));
    }

    public MailboxResult<ContactRelation> DecideContact(MailboxAuthority authority, ContactDecision decision, CancellationToken ct)
    {
        using var db = Session(true, ct);
        var now = _timeProvider.GetUtcNow();
        var request = ReadRequest(db, decision.Request);
        if (request is null)
            return new(MailboxError.Unavailable);
        if (request.Summary.RecipientMailboxId != authority.MailboxId)
            return new(MailboxError.Forbidden);
        var channel = ReadChannel(db, request.Summary.RequesterMailboxId.Value, authority.MailboxId.Value)!;
        if (channel.IsBlocked)
            return new(MailboxError.Forbidden);
        if (channel.Generation != decision.ExpectedGeneration || channel.CurrentRequester != decision.Request.RequesterMailboxId.Value || channel.CurrentRequest != decision.Request.RequestId.Value
            || request.Summary.Generation != channel.Generation)
            return new(MailboxError.Conflict);
        if (decision.Reply is { } candidateReply)
        {
            var invalidReply = ValidatePayload(candidateReply, now, firstAdmission: false);
            if (invalidReply is not null)
                return new(invalidReply.Value);
        }
        var fingerprint = DecisionFingerprint(decision);
        if (request.DecisionFingerprint == fingerprint)
            return new(channel.Relation(request.Summary, request.UpdatedAt));
        var applied = channel.Relation(request.Summary, request.UpdatedAt).ApplyRecipientDecision(decision, now);
        if (applied.Outcome != ContactRelationOutcome.Applied)
            return new(applied.Outcome switch
            {
                ContactRelationOutcome.Expired => MailboxError.Expired,
                ContactRelationOutcome.InvalidDecision => MailboxError.Invalid,
                _ => MailboxError.Conflict
            });
        if (decision.Reply is { } reply)
        {
            var validation = ValidatePayload(reply, now, firstAdmission: true);
            if (validation is not null)
                return new(validation.Value);
            var envelope = new MailboxEnvelope(1, request.Summary.RequesterMailboxId, channel.Generation, reply);
            var admission = AdmitMessage(db, authority.MailboxId, envelope, channel, 2, decision.Request, now);
            if (!admission.IsSuccess)
                return new(admission.Error!.Value);
        }
        db.Execute("""
            UPDATE contact_requests SET status=$status,reply_message=$reply,decision_fingerprint=$fingerprint,
                updated=$now,terminal=$terminal WHERE requester=$requester AND id=$id
            """, ("$status", (int)decision.Status), ("$reply", applied.Relation.Request.ReplyMessageId?.ToString()),
            ("$fingerprint", fingerprint), ("$now", now.UtcTicks),
            ("$terminal", decision.Status == ContactStatus.Rejected ? now.UtcTicks : null), ("$id", decision.Request.RequestId.Value), ("$requester", decision.Request.RequesterMailboxId.Value));
        WriteChannel(db, channel with { UpdatedAt = now });
        db.Commit();
        return new(applied.Relation);
    }
}
