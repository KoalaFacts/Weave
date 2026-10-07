CREATE TABLE mailbox_schema(version INTEGER NOT NULL);
INSERT INTO mailbox_schema VALUES(1);
CREATE TABLE mailbox_owners(mailbox TEXT PRIMARY KEY NOT NULL);
CREATE TABLE contact_cards(
    id TEXT PRIMARY KEY NOT NULL, owner TEXT NOT NULL, visibility INTEGER NOT NULL,
    created INTEGER NOT NULL, expires INTEGER, revoked INTEGER, audience TEXT, methods TEXT NOT NULL,
    FOREIGN KEY(owner) REFERENCES mailbox_owners(mailbox));
CREATE TABLE contact_channels(
    low TEXT NOT NULL, high TEXT NOT NULL, generation INTEGER NOT NULL,
    blocked_low INTEGER NOT NULL, blocked_high INTEGER NOT NULL, current_request TEXT, updated INTEGER NOT NULL,
    PRIMARY KEY(low, high), FOREIGN KEY(low) REFERENCES mailbox_owners(mailbox),
    FOREIGN KEY(high) REFERENCES mailbox_owners(mailbox));
CREATE TABLE contact_requests(
    seq INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT NOT NULL UNIQUE, card TEXT NOT NULL,
    requester TEXT NOT NULL, recipient TEXT NOT NULL, method TEXT NOT NULL, generation INTEGER NOT NULL,
    status INTEGER NOT NULL, created INTEGER NOT NULL, expires INTEGER NOT NULL,
    request_message TEXT NOT NULL, reply_message TEXT, fingerprint TEXT NOT NULL,
    decision_fingerprint TEXT, updated INTEGER NOT NULL, terminal INTEGER);
CREATE INDEX requests_recipient ON contact_requests(recipient, seq);
CREATE INDEX requests_requester ON contact_requests(requester, seq);
CREATE INDEX requests_terminal ON contact_requests(terminal);
CREATE TABLE mailbox_messages(
    seq INTEGER PRIMARY KEY AUTOINCREMENT, sender TEXT NOT NULL, id TEXT NOT NULL,
    recipient TEXT NOT NULL, low TEXT NOT NULL, high TEXT NOT NULL, generation INTEGER NOT NULL,
    version INTEGER NOT NULL, created INTEGER NOT NULL, expires INTEGER NOT NULL, encoding TEXT NOT NULL,
    payload BLOB, payload_size INTEGER NOT NULL, fingerprint TEXT NOT NULL,
    purpose INTEGER NOT NULL, request_id TEXT, state INTEGER NOT NULL, terminal INTEGER,
    UNIQUE(sender, id), FOREIGN KEY(low, high) REFERENCES contact_channels(low, high),
    CHECK((state = 0 AND payload IS NOT NULL AND terminal IS NULL)
        OR (state <> 0 AND payload IS NULL AND terminal IS NOT NULL)));
CREATE INDEX messages_inbox ON mailbox_messages(recipient, state, seq);
CREATE INDEX messages_outbox ON mailbox_messages(sender, seq);
CREATE INDEX messages_terminal ON mailbox_messages(state, terminal);
