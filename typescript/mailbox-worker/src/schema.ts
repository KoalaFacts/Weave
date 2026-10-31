import { sql } from 'drizzle-orm';
import { sqliteTable, text, integer, primaryKey, foreignKey, index, uniqueIndex, check } from 'drizzle-orm/sqlite-core';
import { exactInteger, opaqueBytes } from './sqlite-values.ts';

// Latest target schema. Data transformations and their execution records are separate.
// No compatibility views, synchronization triggers, or numbered schema snapshots.

export const humans = sqliteTable("humans", {
    human_id: text("human_id").notNull().primaryKey(),
    provider: text("provider").notNull(),
    subject: text("subject").notNull(),
    provider_created_at: text("provider_created_at").notNull(),
    display_login: text("display_login").notNull(),
    frozen_at: exactInteger("frozen_at"),
    created_at: exactInteger("created_at").notNull(),
    updated_at: exactInteger("updated_at").notNull(),
}, t => [
    uniqueIndex("humans_subject").on(t.provider, t.subject),
    check("humans_provider",sql`provider='github'`),
    check("humans_subject_decimal",sql`length(subject) BETWEEN 1 AND 20 AND subject GLOB '[1-9]*' AND subject NOT GLOB '*[^0-9]*'`)
]);

export const accounts = sqliteTable("accounts", {
    account_id: text("account_id").notNull().primaryKey(),
    human_id: text("human_id").references(() => humans.human_id, {onDelete:"restrict"}),
    display_name: text("display_name"),
    description: text("description"),
    created_at: exactInteger("created_at"),
    updated_at: exactInteger("updated_at"),
}, t => [
    index("accounts_human").on(t.human_id, t.created_at)
]);

export const agentKeys = sqliteTable("agent_keys", {
    kid: text("kid").notNull().primaryKey(),
    account_id: text("account_id").notNull().references(() => accounts.account_id, {onDelete:"restrict"}),
    public_jwk: text("public_jwk").notNull(),
    label: text("label"),
    created_at: exactInteger("created_at").notNull(),
    last_used_at: exactInteger("last_used_at"),
    revoked_at: exactInteger("revoked_at"),
    cooling_until: exactInteger("cooling_until"),
    updated_at: exactInteger("updated_at").notNull(),
}, t => [
    index("agent_keys_account").on(t.account_id, t.revoked_at),
    check("agent_keys_kid",sql`length(kid)=43 AND kid NOT GLOB '*[^A-Za-z0-9_-]*'`),
    check("agent_keys_public_jwk",sql`json_valid(public_jwk)`),
    check("agent_keys_revocation",sql`revoked_at IS NULL OR revoked_at>=created_at`),
    check("agent_keys_cooling",sql`cooling_until IS NULL OR cooling_until>created_at`),
    check("agent_keys_label",sql`label IS NULL OR length(label) BETWEEN 1 AND 64`)
]);

// Single-use client-assertion IDs, retained until the assertion's own exp.
export const agentAssertions = sqliteTable("agent_assertions", {
    account_id: text("account_id").notNull().references(() => accounts.account_id, {onDelete:"restrict"}),
    jti: text("jti").notNull(),
    expires_at: exactInteger("expires_at").notNull(),
    created_at: exactInteger("created_at").notNull(),
}, t => [
    primaryKey({columns:[t.account_id,t.jti]}),
    index("agent_assertions_expiry").on(t.expires_at)
]);

// Opaque DPoP-bound access tokens, stored only as their hex SHA-256.
export const agentAccessTokens = sqliteTable("agent_access_tokens", {
    token_hash: text("token_hash").notNull().primaryKey(),
    account_id: text("account_id").notNull().references(() => accounts.account_id, {onDelete:"restrict"}),
    kid: text("kid").notNull().references(() => agentKeys.kid, {onDelete:"restrict"}),
    expires_at: exactInteger("expires_at").notNull(),
    created_at: exactInteger("created_at").notNull(),
}, t => [
    index("agent_access_tokens_expiry").on(t.expires_at),
    check("agent_access_tokens_hash",sql`length(token_hash)=64 AND token_hash NOT GLOB '*[^0-9a-f]*'`)
]);

// An ownerless agent's request that a GitHub human own it. No FK to humans: the target may not have signed in yet.
export const bindingRequests = sqliteTable("binding_requests", {
    request_id: text("request_id").notNull().primaryKey(),
    account_id: text("account_id").notNull().references(() => accounts.account_id, {onDelete:"restrict"}),
    target_subject: text("target_subject").notNull(),
    kid: text("kid").notNull().references(() => agentKeys.kid, {onDelete:"restrict"}),
    status: text("status").notNull(),
    created_at: exactInteger("created_at").notNull(),
    expires_at: exactInteger("expires_at").notNull(),
    updated_at: exactInteger("updated_at").notNull(),
    decided_at: exactInteger("decided_at"),
}, t => [
    uniqueIndex("binding_requests_pending").on(t.account_id).where(sql`status='pending'`),
    index("binding_requests_target").on(t.target_subject, t.status, t.expires_at),
    index("binding_requests_account").on(t.account_id, t.created_at),
    index("binding_requests_retention").on(t.status, t.created_at),
    check("binding_requests_subject_decimal",sql`length(target_subject) BETWEEN 1 AND 20 AND target_subject GLOB '[1-9]*' AND target_subject NOT GLOB '*[^0-9]*'`),
    check("binding_requests_status",sql`status IN ('pending','confirmed','rejected','cancelled','expired')`),
    check("binding_requests_expiry",sql`expires_at>created_at`),
    check("binding_requests_decision",sql`(status='pending')=(decided_at IS NULL)`)
]);

// A key added to a human-owned agent by one of its live keys, held until the owner confirms it.
export const keyRequests = sqliteTable("key_requests", {
    request_id: text("request_id").notNull().primaryKey(),
    account_id: text("account_id").notNull().references(() => accounts.account_id, {onDelete:"restrict"}),
    kid: text("kid").notNull(),
    public_jwk: text("public_jwk").notNull(),
    label: text("label"),
    endorsed_by: text("endorsed_by").notNull().references(() => agentKeys.kid, {onDelete:"restrict"}),
    status: text("status").notNull(),
    created_at: exactInteger("created_at").notNull(),
    expires_at: exactInteger("expires_at").notNull(),
    updated_at: exactInteger("updated_at").notNull(),
    decided_at: exactInteger("decided_at"),
}, t => [
    uniqueIndex("key_requests_pending").on(t.account_id, t.kid).where(sql`status='pending'`),
    index("key_requests_account").on(t.account_id, t.created_at),
    index("key_requests_retention").on(t.status, t.created_at),
    check("key_requests_kid",sql`length(kid)=43 AND kid NOT GLOB '*[^A-Za-z0-9_-]*'`),
    check("key_requests_public_jwk",sql`json_valid(public_jwk)`),
    check("key_requests_label",sql`label IS NULL OR length(label) BETWEEN 1 AND 64`),
    check("key_requests_status",sql`status IN ('pending','confirmed','rejected','expired')`),
    check("key_requests_expiry",sql`expires_at>created_at`),
    check("key_requests_decision",sql`(status='pending')=(decided_at IS NULL)`)
]);

// The single current owner-issued recovery code of a human-owned agent, stored only as its hex SHA-256.
// Issuing a new code replaces the row, which invalidates the earlier code.
export const recoveryCodes = sqliteTable("recovery_codes", {
    account_id: text("account_id").notNull().primaryKey().references(() => accounts.account_id, {onDelete:"restrict"}),
    code_hash: text("code_hash").notNull(),
    human_id: text("human_id").notNull().references(() => humans.human_id, {onDelete:"restrict"}),
    created_at: exactInteger("created_at").notNull(),
    expires_at: exactInteger("expires_at").notNull(),
    used_at: exactInteger("used_at"),
    updated_at: exactInteger("updated_at").notNull(),
}, t => [
    uniqueIndex("recovery_codes_hash").on(t.code_hash),
    check("recovery_codes_hash_hex",sql`length(code_hash)=64 AND code_hash NOT GLOB '*[^0-9a-f]*'`),
    check("recovery_codes_expiry",sql`expires_at>created_at`),
    check("recovery_codes_use",sql`used_at IS NULL OR used_at>=created_at`)
]);

export const accountAddresses = sqliteTable("account_addresses", {
    address_id: text("address_id").notNull().primaryKey(),
    account_id: text("account_id").notNull().references(() => accounts.account_id, {onDelete:"restrict"}),
    seq: exactInteger("seq").notNull(),
    label: text("label"),
    is_public: integer("is_public").notNull(),
    created_at: exactInteger("created_at").notNull(),
    effective_from: exactInteger("effective_from"),
    effective_to: exactInteger("effective_to"),
    expires_at: exactInteger("expires_at"),
    revoked_at: exactInteger("revoked_at"),
    direct_uri: text("direct_uri"),
    public_seq: exactInteger("public_seq"),
    legacy_relay_endpoint: text("legacy_relay_endpoint").notNull(),
    updated_at: exactInteger("updated_at"),
}, t => [
    index("addresses_owner").on(t.account_id,t.address_id),
    index("addresses_directory").on(t.is_public,sql`created_at DESC`,sql`address_id DESC`),
    uniqueIndex("addresses_sequence").on(t.seq),
    uniqueIndex("addresses_public_sequence").on(t.public_seq).where(sql`public_seq IS NOT NULL`),
    check("account_addresses_public_flag",sql`is_public IN (0,1)`),
    check("account_addresses_effective_window",sql`effective_from IS NULL OR effective_to IS NULL OR effective_to>effective_from`)
]);

export const contactCards = sqliteTable("contact_cards", {
    card_id: text("card_id").notNull().primaryKey(),
    owner_account_id: text("owner_account_id").notNull().references(() => accounts.account_id, {onDelete:"restrict"}),
    seq: exactInteger("seq").notNull(),
    visibility: text("visibility").notNull(),
    created_at: exactInteger("created_at").notNull(),
    expires_at: exactInteger("expires_at"),
    revoked_at: exactInteger("revoked_at"),
    audience_hint: text("audience_hint"),
    methods: text("methods").notNull(),
    public_seq: exactInteger("public_seq"),
    updated_at: exactInteger("updated_at"),
}, t => [
    index("cards_owner").on(t.owner_account_id,t.seq),
    uniqueIndex("cards_sequence").on(t.seq),
    uniqueIndex("cards_public_sequence").on(t.public_seq).where(sql`public_seq IS NOT NULL`),
    check("contact_cards_visibility",sql`visibility IN ('public','unlisted')`),
    check("contact_cards_expiry",sql`expires_at IS NULL OR expires_at>created_at`),
    check("contact_cards_revocation",sql`revoked_at IS NULL OR revoked_at>=created_at`),
    check("contact_cards_methods",sql`json_valid(methods)`)
]);

export const contactRequests = sqliteTable("contact_requests", {
    requester_account_id: text("requester_account_id").notNull().references(() => accounts.account_id, {onDelete:"restrict"}),
    request_id: text("request_id").notNull(),
    seq: exactInteger("seq").notNull(),
    recipient_account_id: text("recipient_account_id").notNull().references(() => accounts.account_id, {onDelete:"restrict"}),
    request_version: integer("request_version").notNull(),
    requester_address_id: text("requester_address_id").references(() => accountAddresses.address_id, {onDelete:"restrict"}),
    recipient_address_id: text("recipient_address_id").references(() => accountAddresses.address_id, {onDelete:"restrict"}),
    target_card_id: text("target_card_id").references(() => contactCards.card_id, {onDelete:"restrict"}),
    method_id: text("method_id").notNull(),
    legacy_generation: exactInteger("legacy_generation").notNull(),
    status: text("status").notNull(),
    created_at: exactInteger("created_at").notNull(),
    expires_at: exactInteger("expires_at").notNull(),
    request_message_id: text("request_message_id").notNull(),
    reply_message_id: text("reply_message_id"),
    fingerprint: text("fingerprint").notNull(),
    decision_fingerprint: text("decision_fingerprint"),
    updated_at: exactInteger("updated_at").notNull(),
    terminal_at: exactInteger("terminal_at"),
}, t => [
    primaryKey({columns:[t.requester_account_id,t.request_id]}),
    uniqueIndex("requests_sequence").on(t.seq),
    index("requests_sender").on(t.requester_account_id,t.seq),
    index("requests_recipient").on(t.recipient_account_id,t.seq),
    index("requests_pending").on(t.recipient_account_id,t.status,t.expires_at),
    index("requests_address").on(t.recipient_address_id),
    check("contact_requests_different_accounts",sql`requester_account_id<>recipient_account_id`),
    check("contact_requests_target",sql`(recipient_address_id IS NULL)<>(target_card_id IS NULL)`),
    check("contact_requests_request_version",sql`request_version IN (1,2)`),
    check("contact_requests_address_binding",sql`request_version=1 OR (requester_address_id IS NOT NULL AND recipient_address_id IS NOT NULL)`),
    check("contact_requests_expiry",sql`expires_at>created_at`),
    check("contact_requests_generation",sql`legacy_generation>=1`),
    check("contact_requests_status",sql`status IN ('pending','needsAction','accepted','rejected')`)
]);

export const contacts = sqliteTable("contacts", {
    owner_account_id: text("owner_account_id").notNull().references(() => accounts.account_id, {onDelete:"restrict"}),
    to_account_id: text("to_account_id").notNull().references(() => accounts.account_id, {onDelete:"restrict"}),
    connection_token: text("connection_token").notNull(),
    created_at: exactInteger("created_at"),
    updated_at: exactInteger("updated_at"),
}, t => [
    primaryKey({columns:[t.owner_account_id,t.to_account_id]}),
    index("contacts_target").on(t.to_account_id,t.owner_account_id),
    check("contacts_different_accounts",sql`owner_account_id<>to_account_id`),
    check("contacts_connection_token",sql`length(connection_token)=64 AND connection_token NOT GLOB '*[^0-9a-f]*'`)
]);

export const mailboxRecords = sqliteTable("mailbox_records", {
    seq: exactInteger("seq").notNull().primaryKey(),
    message_id: text("message_id").notNull(),
    sender_account_id: text("sender_account_id").notNull().references(() => accounts.account_id, {onDelete:"restrict"}),
    recipient_account_id: text("recipient_account_id").notNull().references(() => accounts.account_id, {onDelete:"restrict"}),
    payload: opaqueBytes("payload"),
    payload_size: integer("payload_size").notNull(),
    payload_encoding: text("payload_encoding"),
    created_at: exactInteger("created_at"),
    expires_at: exactInteger("expires_at").notNull(),
    updated_at: exactInteger("updated_at"),
}, t => [
    uniqueIndex("messages_identity").on(t.sender_account_id,t.message_id),
    index("messages_recipient").on(t.recipient_account_id,t.seq),
    index("messages_sender").on(t.sender_account_id,t.seq),
    index("messages_expiry").on(t.expires_at,t.seq),
    check("mailbox_records_different_accounts",sql`sender_account_id<>recipient_account_id`),
    check("mailbox_records_opaque_payload",sql`payload IS NULL OR typeof(payload)='blob'`),
    check("mailbox_records_payload_size",sql`payload_size>=0 AND payload_size<=65536 AND ((payload IS NULL AND payload_size=0) OR (payload IS NOT NULL AND payload_size=length(payload)))`)
]);

export const messageDelivery = sqliteTable("message_delivery", {
    message_seq: exactInteger("message_seq").notNull().primaryKey().references(() => mailboxRecords.seq, {onDelete:"cascade"}),
    state: text("state").notNull(),
    terminal_at: exactInteger("terminal_at"),
    protocol_version: integer("protocol_version").notNull(),
    purpose: integer("purpose").notNull(),
    legacy_generation: exactInteger("legacy_generation"),
    connection_token: text("connection_token"),
    requester_account_id: text("requester_account_id"),
    request_id: text("request_id"),
    fingerprint: text("fingerprint").notNull(),
    fingerprint_scheme: text("fingerprint_scheme").notNull(),
    created_at: exactInteger("created_at"),
    updated_at: exactInteger("updated_at"),
}, t => [
    index("delivery_terminal").on(t.state,t.terminal_at,t.message_seq),
    index("delivery_request").on(t.requester_account_id,t.request_id),
    check("message_delivery_state",sql`state IN ('pending','acknowledged','expired','blocked')`),
    check("message_delivery_terminal",sql`(state='pending' AND terminal_at IS NULL) OR (state<>'pending' AND terminal_at IS NOT NULL)`),
    check("message_delivery_purpose",sql`purpose IN (0,1,2)`),
    check("message_delivery_request_reference",sql`(requester_account_id IS NULL)=(request_id IS NULL)`),
    check("message_delivery_purpose_reference",sql`(purpose=0 AND request_id IS NULL) OR (purpose IN (1,2) AND request_id IS NOT NULL)`),
    check("message_delivery_fingerprint_scheme",sql`fingerprint_scheme IN ('legacy-envelope-v1','account-envelope-v2')`),
    foreignKey({columns:[t.requester_account_id,t.request_id],foreignColumns:[contactRequests.requester_account_id,contactRequests.request_id]}).onDelete("restrict")
]);

export const legacyChannels = sqliteTable("legacy_channels", {
    low_account_id: text("low_account_id").notNull().references(() => accounts.account_id, {onDelete:"restrict"}),
    high_account_id: text("high_account_id").notNull().references(() => accounts.account_id, {onDelete:"restrict"}),
    generation: exactInteger("generation").notNull(),
    blocked_low: integer("blocked_low").notNull(),
    blocked_high: integer("blocked_high").notNull(),
    current_requester_account_id: text("current_requester_account_id"),
    current_request_id: text("current_request_id"),
    updated_at: exactInteger("updated_at").notNull(),
    created_at: exactInteger("created_at"),
}, t => [
    primaryKey({columns:[t.low_account_id,t.high_account_id]}),
    check("legacy_channels_ordered_pair",sql`low_account_id<high_account_id`),
    check("legacy_channels_generation",sql`generation>=1`),
    check("legacy_channels_blocked_low",sql`blocked_low IN (0,1)`),
    check("legacy_channels_blocked_high",sql`blocked_high IN (0,1)`),
    check("legacy_channels_request_reference",sql`(current_requester_account_id IS NULL)=(current_request_id IS NULL)`),
    foreignKey({columns:[t.current_requester_account_id,t.current_request_id],foreignColumns:[contactRequests.requester_account_id,contactRequests.request_id]}).onDelete("restrict")
]);

export const rateLimits = sqliteTable("rate_limits", {
    seq: exactInteger("seq").notNull().primaryKey(),
    policy: text("policy").notNull(),
    bucket_key: text("bucket_key").notNull(),
    window_start: exactInteger("window_start").notNull(),
    count: integer("count").notNull(),
    created_at: exactInteger("created_at"),
    updated_at: exactInteger("updated_at"),
}, t => [
    uniqueIndex("rate_limit_identity").on(t.policy,t.bucket_key),
    index("rate_limit_window").on(t.policy,t.window_start,t.seq),
    check("rate_limits_policy",sql`policy IN ('registration','public-read','key-recovery','agent-send','agent-contact','agent-token')`),
    check("rate_limits_count",sql`count>=0`)
]);

export const streamLeases = sqliteTable("stream_leases", {
    account_id: text("account_id").notNull().references(() => accounts.account_id, {onDelete:"restrict"}),
    lease_id: text("lease_id").notNull(),
    expires_at: exactInteger("expires_at").notNull(),
    created_at: exactInteger("created_at"),
    updated_at: exactInteger("updated_at"),
}, t => [
    primaryKey({columns:[t.account_id,t.lease_id]}),
    index("leases_expiry").on(t.expires_at),
    index("leases_account").on(t.account_id,t.expires_at)
]);

export const streamCursors = sqliteTable("stream_cursors", {
    account_id: text("account_id").notNull().primaryKey().references(() => accounts.account_id, {onDelete:"restrict"}),
    cursor: text("cursor"),
    version: text("version").notNull(),
    created_at: exactInteger("created_at"),
    updated_at: exactInteger("updated_at"),
});

export const counters = sqliteTable("counters", {
    name: text("name").notNull().primaryKey(),
    value: exactInteger("value").notNull(),
    created_at: exactInteger("created_at"),
    updated_at: exactInteger("updated_at"),
}, t => [
    check("counters_value",sql`value>=0`)
]);

export const maintenanceState = sqliteTable("maintenance_state", {
    name: text("name").notNull().primaryKey(),
    last_run_at: exactInteger("last_run_at").notNull(),
    created_at: exactInteger("created_at"),
    updated_at: exactInteger("updated_at"),
});

export const mailboxRevision = sqliteTable("mailbox_revision", {
    singleton: integer("singleton").notNull().primaryKey(),
    token: text("token").notNull(),
    created_at: exactInteger("created_at"),
    updated_at: exactInteger("updated_at"),
}, t => [
    check("mailbox_revision_singleton",sql`singleton=1`)
]);

export const mailboxGuard = sqliteTable("mailbox_guard", {
    ok: integer("ok"),
    created_at: exactInteger("created_at").notNull(),
    updated_at: exactInteger("updated_at").notNull(),
}, t => [
    check("txn_revision",sql`ok=1`),
]);

export const mailboxRecordsLegacy = sqliteTable("mailbox_records_legacy", {
    seq: integer("seq").notNull().primaryKey({autoIncrement:true}),
    kind: text("kind").notNull(),
    owner: text("owner").notNull(),
    id: text("id").notNull(),
    data: text("data").notNull(),
    archived_at: exactInteger("archived_at").notNull().default(sql`0`),
    payload_cleared_at: exactInteger("payload_cleared_at"),
}, t => [
    uniqueIndex("legacy_identity").on(t.kind,t.owner,t.id),
    index("records_kind").on(t.kind,t.seq),
    index("records_recipient").on(t.kind,sql`json_extract(data,'$.recipientMailboxId')`,t.seq),
    index("records_public").on(t.kind,sql`json_extract(data,'$.visibility')`,t.id),
    index("records_expiry").on(t.kind,sql`json_extract(data,'$.expiresAt')`),
    index("message_recipient").on(t.kind,sql`json_extract(data,'$.receipt.recipientMailboxId')`,t.seq),
    index("address_owner").on(t.kind,sql`json_extract(data,'$.ownerMailboxId')`,t.id),
    check("mailbox_records_legacy_valid_json",sql`json_valid(data)`)
]);


// Independent data-script ledger; d1_migrations remains untouched and unmanaged.
export const dataMigrations = sqliteTable('data_migrations', {
    script_id: text('script_id').primaryKey(),
    checksum: text('checksum').notNull(),
    created_at: exactInteger('created_at').notNull(),
    updated_at: exactInteger('updated_at').notNull(),
    completed_at: exactInteger('completed_at')
});
