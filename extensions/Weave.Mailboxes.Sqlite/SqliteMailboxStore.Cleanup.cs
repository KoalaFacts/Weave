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
        db.Commit();
        return count;
    }
}
