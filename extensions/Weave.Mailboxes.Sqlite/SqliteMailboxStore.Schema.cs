using Microsoft.Data.Sqlite;

namespace Weave.Mailboxes.Sqlite;

public sealed partial class SqliteMailboxStore
{
    private static void InitializeSchema(SqliteConnection connection, bool requireExisting)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        if (!string.Equals(command.ExecuteScalar()?.ToString(), "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Mailbox storage requires SQLite WAL mode.");
        using var transaction = connection.BeginTransaction(deferred: false);
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='mailbox_schema'";
        var exists = (long)command.ExecuteScalar()! != 0;
        if (requireExisting && !exists)
            throw new InvalidOperationException("The retained mailbox schema is missing.");
        if (exists)
        {
            command.CommandText = "SELECT version FROM mailbox_schema";
            var version = (long)command.ExecuteScalar()!;
            if (version == 1)
                MigrateV1(command);
            else if (version != 2)
                throw new InvalidOperationException("Unsupported mailbox schema version.");
            transaction.Commit();
            return;
        }
        command.CommandText = Schema;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private const string Schema = """
        CREATE TABLE mailbox_schema(version INTEGER NOT NULL);
        INSERT INTO mailbox_schema VALUES(2);
        CREATE TABLE mailbox_owners(mailbox TEXT PRIMARY KEY NOT NULL);
        CREATE TABLE contact_cards(
            id TEXT PRIMARY KEY NOT NULL, owner TEXT NOT NULL, visibility INTEGER NOT NULL,
            created INTEGER NOT NULL, expires INTEGER, revoked INTEGER, audience TEXT, methods TEXT NOT NULL,
            FOREIGN KEY(owner) REFERENCES mailbox_owners(mailbox));
        CREATE TABLE contact_channels(
            low TEXT NOT NULL, high TEXT NOT NULL, generation INTEGER NOT NULL,
            blocked_low INTEGER NOT NULL, blocked_high INTEGER NOT NULL, current_request TEXT, current_requester TEXT, updated INTEGER NOT NULL,
            PRIMARY KEY(low, high), FOREIGN KEY(low) REFERENCES mailbox_owners(mailbox),
            FOREIGN KEY(high) REFERENCES mailbox_owners(mailbox));
        CREATE TABLE contact_requests(
            seq INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT NOT NULL, card TEXT NOT NULL,
            requester TEXT NOT NULL, recipient TEXT NOT NULL, method TEXT NOT NULL, generation INTEGER NOT NULL,
            status INTEGER NOT NULL, created INTEGER NOT NULL, expires INTEGER NOT NULL,
            request_message TEXT NOT NULL, reply_message TEXT, fingerprint TEXT NOT NULL,
            decision_fingerprint TEXT, updated INTEGER NOT NULL, terminal INTEGER, UNIQUE(requester,id));
        CREATE INDEX requests_recipient ON contact_requests(recipient, seq);
        CREATE INDEX requests_requester ON contact_requests(requester, seq);
        CREATE INDEX requests_terminal ON contact_requests(terminal);
        CREATE TABLE mailbox_messages(
            seq INTEGER PRIMARY KEY AUTOINCREMENT, sender TEXT NOT NULL, id TEXT NOT NULL,
            recipient TEXT NOT NULL, low TEXT NOT NULL, high TEXT NOT NULL, generation INTEGER NOT NULL,
            version INTEGER NOT NULL, created INTEGER NOT NULL, expires INTEGER NOT NULL, encoding TEXT NOT NULL,
            payload BLOB, payload_size INTEGER NOT NULL, fingerprint TEXT NOT NULL,
            purpose INTEGER NOT NULL, request_id TEXT, request_requester TEXT, state INTEGER NOT NULL, terminal INTEGER,
            UNIQUE(sender, id), FOREIGN KEY(low, high) REFERENCES contact_channels(low, high),
            CHECK((state = 0 AND payload IS NOT NULL AND terminal IS NULL)
                OR (state <> 0 AND payload IS NULL AND terminal IS NOT NULL)));
        CREATE INDEX messages_inbox ON mailbox_messages(recipient, state, seq);
        CREATE INDEX messages_outbox ON mailbox_messages(sender, seq);
        CREATE INDEX messages_terminal ON mailbox_messages(state, terminal);
        """;
}
