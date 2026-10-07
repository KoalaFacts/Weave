using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Weave.Contacts;

namespace Weave.Mailboxes.Sqlite;

public sealed partial class SqliteMailboxStore
{
    public ContactRequestSummary? GetContactRequest(MailboxAuthority authority, ContactRequestLocator request, CancellationToken ct)
    {
        using var db = Session(false, ct);
        var row = ReadRequest(db, request);
        return row is not null && (row.Summary.RequesterMailboxId == authority.MailboxId
            || row.Summary.RecipientMailboxId == authority.MailboxId) ? row.Summary : null;
    }

    public MailboxPage<ContactRequestSummary> ListContactRequests(MailboxAuthority authority, string? afterCursor, int limit, CancellationToken ct)
    {
        using var db = Session(false, ct);
        if (!PageStart("contacts", authority, afterCursor, limit, out var after))
            return new([], null);
        var rows = db.Query(RequestSelect + " WHERE (requester=$owner OR recipient=$owner) AND seq>$after ORDER BY seq LIMIT $limit",
            MapRequest, ("$owner", authority.MailboxId.Value), ("$after", after), ("$limit", limit + 1));
        return Page(rows, row => row.Sequence, row => row.Summary, "contacts", authority, limit);
    }

    public MailboxReceipt? GetReceipt(MailboxAuthority authority, Guid messageId, CancellationToken ct)
    {
        using var db = Session(false, ct);
        var row = ReadMessage(db, authority.MailboxId, messageId);
        return row is null ? null : EffectiveReceipt(db, row, _timeProvider.GetUtcNow());
    }

    public MailboxPage<MailboxReceipt> ListReceipts(MailboxAuthority authority, string? afterCursor, int limit, CancellationToken ct)
    {
        using var db = Session(false, ct);
        if (!PageStart("receipts", authority, afterCursor, limit, out var after))
            return new([], null);
        var now = _timeProvider.GetUtcNow();
        var rows = db.Query(MessageSelect + " WHERE sender=$owner AND seq>$after ORDER BY seq LIMIT $limit",
            MapMessage, ("$owner", authority.MailboxId.Value), ("$after", after), ("$limit", limit + 1));
        return Page(rows, row => row.Sequence, row => EffectiveReceipt(db, row, now), "receipts", authority, limit);
    }

    public MailboxPage<ReceivedMailboxMessage> ReadPending(MailboxAuthority authority, string? afterCursor, int limit, CancellationToken ct)
    {
        using var db = Session(false, ct);
        if (!PageStart("inbox", authority, afterCursor, limit, out var after))
            return new([], null);
        var now = _timeProvider.GetUtcNow();
        var rows = db.Query("""
            SELECT m.seq,m.sender,m.recipient,m.version,m.generation,m.id,m.created,m.expires,m.encoding,m.payload
            FROM mailbox_messages m JOIN contact_channels c ON m.low=c.low AND m.high=c.high
            WHERE m.recipient=$owner AND m.seq>$after AND m.state=0 AND m.expires>$now
                AND c.blocked_low=0 AND c.blocked_high=0 AND c.generation=m.generation
                AND ((m.purpose IN (1,2) AND m.request_requester=c.current_requester AND m.request_id=c.current_request)
                    OR (m.purpose=0 AND EXISTS(SELECT 1 FROM contact_requests r
                        WHERE r.requester=c.current_requester AND r.id=c.current_request AND r.status=2 AND r.generation=c.generation)))
            ORDER BY m.seq LIMIT $limit
            """, r => (Sequence: r.GetInt64(0), Message: new ReceivedMailboxMessage(new(r.GetString(1)),
                new(r.GetInt32(3), new(r.GetString(2)), r.GetInt64(4),
                    new(Guid.Parse(r.GetString(5)), Time(r.GetInt64(6)), Time(r.GetInt64(7)), r.GetString(8), (byte[])r[9])))),
            ("$owner", authority.MailboxId.Value), ("$after", after), ("$now", now.UtcTicks), ("$limit", limit + 1));
        return Page(rows, row => row.Sequence, row => row.Message, "inbox", authority, limit);
    }

    private const string RequestSelect = """
        SELECT seq,id,card,requester,recipient,method,generation,status,created,expires,request_message,
            reply_message,fingerprint,decision_fingerprint,updated,terminal FROM contact_requests
        """;

    private static MailboxRequestRow MapRequest(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetInt64(0),
        new(new(r.GetString(1)), new(r.GetString(2)), new(r.GetString(3)), new(r.GetString(4)), r.GetString(5),
            r.GetInt64(6), (ContactStatus)r.GetInt32(7), Time(r.GetInt64(8)), Time(r.GetInt64(9)),
            Guid.Parse(r.GetString(10)), r.IsDBNull(11) ? null : Guid.Parse(r.GetString(11))),
        r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13), Time(r.GetInt64(14)), r.IsDBNull(15) ? null : Time(r.GetInt64(15)));

    private static MailboxRequestRow? ReadRequest(MailboxDatabaseSession db, ContactRequestLocator request) =>
        db.Query(RequestSelect + " WHERE requester=$requester AND id=$id", MapRequest,
            ("$requester", request.RequesterMailboxId.Value), ("$id", request.RequestId.Value)).SingleOrDefault();

    private const string MessageSelect = """
        SELECT seq,id,sender,recipient,state,expires,terminal,generation,fingerprint FROM mailbox_messages
        """;

    private static MailboxMessageRow MapMessage(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetInt64(0),
        new(Guid.Parse(r.GetString(1)), new(r.GetString(2)), new(r.GetString(3)), (MailboxReceiptState)r.GetInt32(4),
            Time(r.GetInt64(5)), r.IsDBNull(6) ? null : Time(r.GetInt64(6))), r.GetInt64(7), r.GetString(8));

    private static MailboxMessageRow? ReadMessage(MailboxDatabaseSession db, MailboxId sender, Guid id) =>
        db.Query(MessageSelect + " WHERE sender=$sender AND id=$id", MapMessage,
            ("$sender", sender.Value), ("$id", id.ToString())).SingleOrDefault();

    private static bool PageStart(string kind, MailboxAuthority authority, string? cursor, int limit, out long after)
    {
        after = 0;
        if (!ValidId(authority.MailboxId.Value) || limit is < 1 or > 100)
            return false;
        if (cursor is null)
            return true;
        var prefix = CursorPrefix(kind, authority);
        return cursor.StartsWith(prefix, StringComparison.Ordinal)
            && long.TryParse(cursor.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out after) && after >= 0;
    }

    private static string CursorPrefix(string kind, MailboxAuthority authority) =>
        kind + ":" + Convert.ToBase64String(Encoding.UTF8.GetBytes(authority.MailboxId.Value)) + ":";

    private static MailboxPage<T> Page<TRow, T>(List<TRow> rows, Func<TRow, long> sequence,
        Func<TRow, T> project, string kind, MailboxAuthority authority, int limit)
    {
        var next = rows.Count > limit ? CursorPrefix(kind, authority) + sequence(rows[limit - 1]).ToString(CultureInfo.InvariantCulture) : null;
        return new(rows.Take(limit).Select(project).ToImmutableArray(), next);
    }
}
