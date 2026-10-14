CREATE TABLE mailbox_revision (singleton INTEGER PRIMARY KEY CHECK(singleton=1), token TEXT NOT NULL);
INSERT INTO mailbox_revision VALUES(1, 'initial');
CREATE TABLE mailbox_guard (ok INTEGER CONSTRAINT txn_revision CHECK(ok=1));
CREATE TABLE mailbox_records (
    seq INTEGER PRIMARY KEY AUTOINCREMENT,
    kind TEXT NOT NULL,
    owner TEXT NOT NULL,
    id TEXT NOT NULL,
    data TEXT NOT NULL CHECK(json_valid(data)),
    UNIQUE(kind,owner,id)
);
CREATE INDEX records_kind ON mailbox_records(kind,seq);
CREATE INDEX records_recipient ON mailbox_records(kind,json_extract(data,'$.recipientMailboxId'),seq);
CREATE INDEX records_public ON mailbox_records(kind,json_extract(data,'$.visibility'),id);
CREATE INDEX records_control ON mailbox_records(kind,json_extract(data,'$.controlHash'));
CREATE INDEX records_expiry ON mailbox_records(kind,json_extract(data,'$.expiresAt'));

CREATE UNIQUE INDEX account_control_hash ON mailbox_records(json_extract(data,'$.controlHash')) WHERE kind='account';
CREATE INDEX message_recipient ON mailbox_records(kind,json_extract(data,'$.receipt.recipientMailboxId'),seq);
CREATE INDEX address_owner ON mailbox_records(kind,json_extract(data,'$.ownerMailboxId'),id);
