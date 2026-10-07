using Microsoft.Data.Sqlite;

namespace Weave.Mailboxes.Sqlite;

public sealed partial class SqliteMailboxStore
{
    private static void MigrateV1(SqliteCommand command)
    {
        command.CommandText = """
            ALTER TABLE contact_channels ADD COLUMN current_requester TEXT;
            UPDATE contact_channels SET current_requester=(SELECT requester FROM contact_requests WHERE id=current_request)
                WHERE current_request IS NOT NULL;
            ALTER TABLE mailbox_messages ADD COLUMN request_requester TEXT;
            UPDATE mailbox_messages SET request_requester=CASE WHEN purpose=1 THEN sender WHEN purpose=2 THEN recipient END
                WHERE request_id IS NOT NULL;
            ALTER TABLE contact_requests RENAME TO contact_requests_v1;
            CREATE TABLE contact_requests(
                seq INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT NOT NULL, card TEXT NOT NULL,
                requester TEXT NOT NULL, recipient TEXT NOT NULL, method TEXT NOT NULL, generation INTEGER NOT NULL,
                status INTEGER NOT NULL, created INTEGER NOT NULL, expires INTEGER NOT NULL,
                request_message TEXT NOT NULL, reply_message TEXT, fingerprint TEXT NOT NULL,
                decision_fingerprint TEXT, updated INTEGER NOT NULL, terminal INTEGER, UNIQUE(requester,id));
            INSERT INTO contact_requests SELECT * FROM contact_requests_v1;
            INSERT INTO sqlite_sequence(name,seq) SELECT 'contact_requests',seq FROM sqlite_sequence
                WHERE name='contact_requests_v1' AND NOT EXISTS(SELECT 1 FROM sqlite_sequence WHERE name='contact_requests');
            UPDATE sqlite_sequence SET seq=MAX(seq,COALESCE((SELECT seq FROM sqlite_sequence WHERE name='contact_requests_v1'),0))
                WHERE name='contact_requests';
            DROP TABLE contact_requests_v1;
            CREATE INDEX requests_recipient ON contact_requests(recipient,seq);
            CREATE INDEX requests_requester ON contact_requests(requester,seq);
            CREATE INDEX requests_terminal ON contact_requests(terminal);
            UPDATE mailbox_schema SET version=2;
            """;
        command.ExecuteNonQuery();
    }
}
