using Weave.Contacts;

namespace Weave.Mailboxes.Sqlite;

public sealed partial class SqliteMailboxStore
{
    public ContactChannelSnapshot? GetContactChannel(MailboxAuthority authority, MailboxId peerMailboxId, CancellationToken ct)
    {
        using var db = Session(false, ct);
        return ReadChannel(db, authority.MailboxId.Value, peerMailboxId.Value)?.Snapshot(authority.MailboxId);
    }

    public MailboxResult<ContactChannelSnapshot> SetBlocked(MailboxAuthority authority, MailboxId peerMailboxId,
        long expectedGeneration, bool blocked, CancellationToken ct)
    {
        using var db = Session(true, ct);
        var now = _timeProvider.GetUtcNow();
        if (!ValidId(authority.MailboxId.Value) || !ValidId(peerMailboxId.Value)
            || authority.MailboxId == peerMailboxId)
            return new(MailboxError.Invalid);
        var channel = ReadChannel(db, authority.MailboxId.Value, peerMailboxId.Value);
        if (channel is null)
            return new(MailboxError.Unavailable);
        if (channel.Generation != expectedGeneration)
            return new(MailboxError.Conflict);
        var snapshot = channel.Snapshot(authority.MailboxId);
        if (snapshot.IsBlockedByMailbox == blocked)
            return new(snapshot);
        if (channel.Generation == long.MaxValue)
            return new(MailboxError.Capacity);
        var isLow = channel.Low == authority.MailboxId.Value;
        var updated = channel with
        {
            Generation = channel.Generation + 1,
            BlockedLow = isLow ? blocked : channel.BlockedLow,
            BlockedHigh = isLow ? channel.BlockedHigh : blocked,
            CurrentRequest = null,
            CurrentRequester = null,
            UpdatedAt = now
        };
        InvalidateChannel(db, channel, now);
        WriteChannel(db, updated);
        db.Commit();
        return new(updated.Snapshot(authority.MailboxId));
    }

    private static (string Low, string High) Pair(string first, string second) =>
        string.CompareOrdinal(first, second) < 0 ? (first, second) : (second, first);

    private static MailboxChannelRow? ReadChannel(MailboxDatabaseSession db, string first, string second)
    {
        var (low, high) = Pair(first, second);
        return db.Query("SELECT low,high,generation,blocked_low,blocked_high,current_request,current_requester,updated FROM contact_channels WHERE low=$low AND high=$high",
            r => new MailboxChannelRow(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetBoolean(3), r.GetBoolean(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), Time(r.GetInt64(7))), ("$low", low), ("$high", high)).SingleOrDefault();
    }

    private static void WriteChannel(MailboxDatabaseSession db, MailboxChannelRow channel) => db.Execute("""
        INSERT INTO contact_channels(low,high,generation,blocked_low,blocked_high,current_request,current_requester,updated)
        VALUES($low,$high,$generation,$blockedLow,$blockedHigh,$request,$requester,$updated)
        ON CONFLICT(low,high) DO UPDATE SET generation=$generation,blocked_low=$blockedLow,
        blocked_high=$blockedHigh,current_request=$request,current_requester=$requester,updated=$updated
        """, ("$low", channel.Low), ("$high", channel.High), ("$generation", channel.Generation),
        ("$blockedLow", channel.BlockedLow), ("$blockedHigh", channel.BlockedHigh),
        ("$request", channel.CurrentRequest), ("$requester", channel.CurrentRequester), ("$updated", channel.UpdatedAt.UtcTicks));

    private static void InvalidateChannel(MailboxDatabaseSession db, MailboxChannelRow channel, DateTimeOffset now)
    {
        db.Execute("""
            UPDATE mailbox_messages SET payload=NULL,payload_size=0,
                state=CASE WHEN expires<=$now THEN 2 ELSE 3 END,
                terminal=CASE WHEN expires<=$now THEN expires ELSE $now END
            WHERE low=$low AND high=$high AND state=0
            """, ("$low", channel.Low), ("$high", channel.High), ("$now", now.UtcTicks));
        if (channel.CurrentRequest is not null)
            db.Execute("UPDATE contact_requests SET terminal=COALESCE(terminal,$now) WHERE requester=$requester AND id=$id",
                ("$id", channel.CurrentRequest), ("$requester", channel.CurrentRequester), ("$now", now.UtcTicks));
    }
}
