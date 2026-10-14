# Worker account storage and retained contact history

The optional Cloudflare Worker now stores accounts, permanent addresses, contact requests, directional contacts and opaque message bytes in separate D1 tables. This change does not migrate or change the C# SQLite provider. `hosts/Weave.Mailbox.Worker/src/schema.ts` is the maintained Drizzle structure; it contains 16 business tables and 128 columns, plus the separate five-column `data_migrations` execution ledger.

## Preserved data and compatibility

The one-time `normalize-mailbox-records` script renames the existing JSON table to `mailbox_records_legacy`, retaining its original five columns, rows, sequence numbers and indexes. It adds archive-operation metadata, without inventing missing historical creation/update times. `d1_migrations` remains untouched. The new record counter preserves the old SQLite sequence high-water, including deleted IDs. The archive is a verification/history source, never the active business store or a recovery snapshot. ACK, blocking and expiry clear corresponding archived message bodies along with current bodies; the archived rows remain.

All new point-in-time columns use UTC Unix epoch 100ns INTEGER values without unit suffixes. D1 integer reads explicitly cast to decimal text and bindings use decimal strings; 64-bit values do not pass through JavaScript Number. Millisecond conversion is confined to existing wire/runtime boundaries. New server rows share an operation time, unchanged retries preserve row timestamps, and older unknown timestamps remain NULL. The schema and runtime retain revision/CAS fencing independently of timestamps.

Canonical HTTP and demo names use `accountId`, `*AccountId` and `X-Weave-Account-Control`. The previous identity fields/header, MCP environment variable and old tool argument shapes remain for one release. Conflicting identity/header aliases are rejected. A client using only the old control header receives the original v1 response shape (including SSE), preserving strict original C# DTO readers; the canonical header exposes the new account/address metadata. Existing controls retain their hash scheme/parameters and IDs. Original v1 fingerprints, cursors and generation integers remain usable; the C# v1 interoperability test remains required.

## Contact and payload behavior

Modern requests bind both the authenticated requester's account/address pair and the recipient's account/address pair. Legacy requests retain a NULL source address rather than guessing one. An address ID and owner are permanent; revocation retains the row. The owner can list revoked history explicitly, and the demo does so.

Acceptance creates two owner-scoped contact rows with the same fresh 256-bit round token. Sending requires both rows and the current token/channel. Removing a contact deletes only that owner's row, invalidates delivery in both directions, and retains the other owner's row and every request. Reconnection requires a new accepted request and a new token. Replaying an old acceptance cannot restore a deleted contact.

Decoded message content is an opaque BLOB, including empty bytes, with a 65536-byte limit. Base64 is a JSON transport representation. The whole JSON body is bounded to 131072 bytes. Oversize payload validation runs before maintenance or business writes. Cleared content is NULL, distinct from an empty BLOB; receipts and request history retain their separate lifetimes.

## Deployment and recovery

The deployment script verifies the existing `demo` Worker, account subdomain and its single existing `db` binding. It builds the active and recovery bundles, then deploys and verifies maintenance mode before conversion. The conversion's temporary `mailbox_guard` trigger additionally fences in-flight old writers, including stream release/progress and rate updates. Errors retain that gate; resumptions require the same script checksum. Verification compares normalized rows, source/history digests, foreign keys and preserved high-water before recording completion.

Schema plans derive from the latest Drizzle declaration and compare current column, foreign-key, CHECK and index definitions. Missing tables/indexes can be created. Existing structural drift, destructive operations and unsupported DDL stop before application; no table reconstruction or numbered SQL schema chain is used. The old baseline SQL lives only under `test/fixtures`.

The Node migration runner uses Wrangler's official remote D1 binding, preserving transactional `db.batch()`. It reads bounded pages and yields to the Node proxy's I/O timers before batches; it never retries an unknown write automatically. Deployment uses the ordinary approved Cloudflare environment, not a new public migration endpoint or a newly created permanent credential.

Before active cutover, deployment retains a separate schema-aware recovery version and records its ID, bundle hashes and exact command. Recovery reads the current normalized tables, including post-conversion registrations, revoked addresses, accepted/deleted contacts, ACKs and cleanup results. It rejects business writes and streaming, and performs no scheduled cleanup or public rate writes. This provides read-only recovery availability. Never select the pre-conversion Worker or overwrite newer data with a frozen database snapshot.

## Verification boundaries

The regression suite exercises real workerd/D1 conversion, interruption/resume, exact integers beyond Number precision, raw/empty/cleared BLOBs, preserved migration history, unchanged retries, oversize rejection before writes, concurrent send/delete/old-acceptance replay, sequence exhaustion, and recovery after new writes. A separate local rehearsal generated data through the actual prior Worker bundle before conversion and retained all three bundle hashes.

The browser suite covers independent registration, public/private discovery, source/destination address binding, recipient acceptance, an exact retry after a lost successful send response, two-way messages and mobile ACK, revoked address history and owner-only contact removal. Inspect the screenshots as well as assertions. An explicitly approved local functional profile does not execute Chromium protection assertions and is not protected-browser acceptance; the repository and CI sandbox gate remain unchanged.

Self-review used the repository `check-rules` and `adversarial-review` procedures. It found and corrected pre-413 maintenance writes, no-op timestamp refreshes, inconsistent creation/update operation times, and recovery directory rate writes. Local passes do not establish an authorized remote migration or deployment. The final commit, CI result and actual publication must be reported separately.
