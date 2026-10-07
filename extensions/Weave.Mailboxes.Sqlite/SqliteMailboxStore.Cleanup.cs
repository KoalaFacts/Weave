namespace Weave.Mailboxes.Sqlite;

public sealed partial class SqliteMailboxStore
{
    public int Sweep(int batchSize, CancellationToken ct)
    {
        using var db = Session(true, ct);
        if (batchSize is < 1 or > 1000)
            return 0;
        var now = _timeProvider.GetUtcNow();
        var cutoff = now.UtcTicks - _options.TerminalRetention.Ticks;
        var messages = db.Query(MessageSelect + " WHERE state=0 AND expires<=$now ORDER BY seq LIMIT $limit",
            MapMessage, ("$now", now.UtcTicks), ("$limit", batchSize));
        foreach (var message in messages)
            TerminalizeEffective(db, message, now);
        var count = messages.Count;
        count += db.Execute("""
            DELETE FROM mailbox_messages WHERE seq IN (SELECT seq FROM mailbox_messages
                WHERE state<>0 AND terminal<=$cutoff ORDER BY seq LIMIT $limit)
            """, ("$cutoff", cutoff), ("$limit", batchSize - count));
        count += db.Execute("""
            UPDATE contact_requests SET terminal=expires WHERE seq IN (SELECT seq FROM contact_requests
                WHERE status IN (0,1) AND terminal IS NULL AND expires<=$now ORDER BY seq LIMIT $limit)
            """, ("$now", now.UtcTicks), ("$limit", batchSize - count));
        var requests = db.Query("""
            SELECT requester,id FROM contact_requests WHERE terminal<=$cutoff ORDER BY seq LIMIT $limit
            """, r => new Weave.Contacts.ContactRequestLocator(new(r.GetString(0)), new(r.GetString(1))), ("$cutoff", cutoff), ("$limit", batchSize - count));
        foreach (var request in requests)
        {
            db.Execute("UPDATE contact_channels SET current_request=NULL,current_requester=NULL WHERE current_requester=$requester AND current_request=$id",
                ("$id", request.RequestId.Value), ("$requester", request.RequesterMailboxId.Value));
            count += db.Execute("DELETE FROM contact_requests WHERE requester=$requester AND id=$id",
                ("$id", request.RequestId.Value), ("$requester", request.RequesterMailboxId.Value));
        }
        db.Commit();
        return count;
    }
}
