using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Weave.Contacts;
using Weave.Mailboxes.Sqlite;

namespace Weave.Mailboxes.Tests.Storage;

internal sealed class MailboxV1Fixture : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "weave-mailbox-v1-" + Guid.NewGuid());
    internal MailboxV1Fixture()
    {
        Directory.CreateDirectory(_directory);
        Options = new() { DatabasePath = Path.Combine(_directory, "retained.db"), RequireExistingStorage = true };
        Request = new(new(Guid.CreateVersion7(Clock.GetUtcNow()).ToString()), new("card"), "relay", Payload("handshake"));
        Reply = Payload("reply"); Live = Payload("live");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Options.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        Execute(connection, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Storage/Fixtures/mailbox-v1.sql")));
        Execute(connection, "INSERT INTO mailbox_owners VALUES('alice'),('bob'),('eve')");
        Execute(connection, "INSERT INTO contact_cards VALUES('card','bob',1,$now,NULL,NULL,NULL,$methods)",
            ("$now", Clock.GetUtcNow().UtcTicks), ("$methods", JsonSerializer.Serialize(new[] { new ContactMethod("relay", 1, "relay", "https://relay.example.test", "opaque") })));
        Execute(connection, "INSERT INTO contact_channels VALUES('alice','bob',1,0,0,$id,$now)", ("$id", Request.RequestId.Value), ("$now", Clock.GetUtcNow().UtcTicks));
        Execute(connection, "INSERT INTO contact_channels VALUES('bob','eve',3,0,1,NULL,$now)", ("$now", Clock.GetUtcNow().UtcTicks));
        var requestHash = Hash(w => { w.Write("card"); w.Write("relay"); Write(w, Request.Payload); });
        var decisionHash = Hash(w => { w.Write(Request.RequestId.Value); w.Write(1L); w.Write(2); w.Write(true); Write(w, Reply); });
        Execute(connection, """
            INSERT INTO contact_requests VALUES(1,$id,'card','alice','bob','relay',1,2,$created,$expires,$message,$reply,$hash,$decision,$created,NULL)
            """, ("$id", Request.RequestId.Value), ("$created", Request.Payload.CreatedAt.UtcTicks), ("$expires", Request.Payload.ExpiresAt.UtcTicks),
            ("$message", Request.Payload.MessageId.ToString()), ("$reply", Reply.MessageId.ToString()), ("$hash", requestHash), ("$decision", decisionHash));
        Message(connection, 1, "alice", "bob", Request.Payload, 1, 1);
        Message(connection, 2, "bob", "alice", Reply, 2, 1);
        Message(connection, 3, "alice", "bob", Live, 0, 0);
        Execute(connection, "UPDATE sqlite_sequence SET seq=12 WHERE name='contact_requests'");
    }
    internal MailboxOptions Options { get; }
    internal MailboxTestClock Clock { get; } = new();
    internal ContactRequestSubmission Request { get; }
    internal MailboxPayload Reply { get; }
    internal MailboxPayload Live { get; }
    internal SqliteMailboxStore Open() => new(Options, Clock);
    internal long Scalar(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Options.DatabasePath, Pooling = false }.ToString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
    internal void SetMigrationFailure(bool enabled)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Options.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        Execute(connection, enabled ? "CREATE TRIGGER reject_migration BEFORE UPDATE ON mailbox_schema BEGIN SELECT RAISE(ABORT,'synthetic migration failure'); END" : "DROP TRIGGER reject_migration");
    }
    private MailboxPayload Payload(string value)
    {
        var now = Clock.GetUtcNow(); return new(Guid.CreateVersion7(now), now, now.AddMinutes(20), "opaque", Encoding.UTF8.GetBytes(value));
    }
    private void Message(SqliteConnection connection, int sequence, string sender, string recipient, MailboxPayload payload, int purpose, int state)
    {
        var hash = Hash(w => { w.Write(1); w.Write(recipient); w.Write(1L); w.Write(purpose); w.Write(purpose == 0 ? "" : Request.RequestId.Value); Write(w, payload); });
        Execute(connection, """
            INSERT INTO mailbox_messages VALUES($seq,$sender,$id,$recipient,'alice','bob',1,1,$created,$expires,'opaque',
                $payload,$size,$hash,$purpose,$request,$state,$terminal)
            """, ("$seq", sequence), ("$sender", sender), ("$id", payload.MessageId.ToString()), ("$recipient", recipient),
            ("$created", payload.CreatedAt.UtcTicks), ("$expires", payload.ExpiresAt.UtcTicks), ("$payload", state == 0 ? payload.Bytes.ToArray() : null),
            ("$size", state == 0 ? payload.Bytes.Length : 0), ("$hash", hash), ("$purpose", purpose),
            ("$request", purpose == 0 ? null : Request.RequestId.Value), ("$state", state), ("$terminal", state == 0 ? null : payload.CreatedAt.UtcTicks));
    }
    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] values)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
    private static string Hash(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream(); using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) write(writer);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    private static void Write(BinaryWriter writer, MailboxPayload payload)
    {
        writer.Write(payload.MessageId.ToByteArray()); writer.Write(payload.CreatedAt.UtcTicks); writer.Write(payload.ExpiresAt.UtcTicks);
        writer.Write(payload.PayloadEncoding); writer.Write(payload.Bytes.Length); writer.Write(payload.Bytes.AsSpan());
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
