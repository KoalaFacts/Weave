# Weave mailbox demo on a Worker and D1

A small native TypeScript Worker with a browser demo at `/`, D1 persistence, and the existing [mailbox v1 HTTP protocol](../../protocol/weave-mailbox/v1/README.md). There is no Express server, hosted reasoning loop, automatic agent wakeup, forwarding to direct URIs, or production deployment in this change. Registration is disabled by default. The checked-in D1 ID is a placeholder, not a live database.

## Run locally

Use Node 24.19+ and the repository's .NET SDK policy. From this directory:

```sh
npm ci
npm run typecheck
npm test
npx wrangler d1 migrations apply db --local
npm run dev -- --ip 127.0.0.1 --port 8787 --var REGISTRATION_ENABLED:true
```

Open `http://127.0.0.1:8787/`. Use two separate browser sessions for two agents. Generate and save each control locally, register, create an address on one agent, and use that address to request contact from the other. The recipient accepts explicitly; the sender can then send. Refresh or watch the recipient inbox and ACK a message. Refresh the sender's outbox to see its terminal receipt.

The page holds controls and uncertain-send retry bodies only in memory. Closing or clearing the page loses that local state; keep your control in your own secure store. An uncertain send retries the same envelope while the page remains open. Readable demo text is not end-to-end encrypted. A public address permits a contact attempt, not an accepted connection. `directUri` is displayed as text metadata and is never fetched or forwarded to.

For C# interoperability, from the repository root build the solution normally. Then, from this directory:

```sh
npm run test:compat
```

This starts real local workerd/D1, runs `tests/Weave.Worker.Compatibility`, then shuts down the test server. The executable links the original C# v1 DTO/source-generated JSON source files rather than maintaining copies. Its UTF-8 hash vectors are deliberately synthetic public test data. Standard `dotnet test` does not run this separate cross-runtime fixture; run the command above as well.

No account token, Cloudflare secret or real registration data is needed for these commands. Do not run remote migration/deployment commands until the target account, D1 resource and deployment have been separately authorized.

### D1 resource and table setup

The operator creates the Worker and D1 resources separately. In `wrangler.jsonc`, keep the D1 binding `db`, replace `database_name` with the actual database name and `database_id` with its ID. The all-zero checked-in ID is a placeholder. The Worker name is `demo`, matching the existing resource; do not create a duplicate Worker. Registration remains disabled until explicitly enabled. The page uses relative API paths and new relay addresses default to the actual request URL origin. No GitHub `WEAVE_HOST` variable is required. An optional `WEAVE_HOST` may specify a validated canonical HTTPS origin (HTTP only on loopback), with no credentials, path, query or fragment. Forwarded host/protocol headers are not used to generate links.

In the Cloudflare dashboard, open the existing `demo` Worker, select **Bindings → Add binding → D1 database**, set the variable name to **db**, and choose the independent Weave database. Keep that binding consistent with the checked-in Wrangler configuration. The runtime accesses `env.db` through Cloudflare's binding; no database password or Cloudflare management API token belongs in the app. Deployment and remote migrations instead require the operator/CI's separately authorized Cloudflare management credentials and account ID. See [D1 bindings](https://developers.cloudflare.com/d1/get-started/#3-bind-your-worker-to-your-d1-database). Do not share another application's business tables.

Tables are created by the versioned SQL in `migrations/`, not automatically on requests. From this directory, after the remote resource and migration have been authorized, run:

```sh
npx wrangler d1 migrations list db --remote
npx wrangler d1 migrations apply db --remote
```

These are operator instructions, not commands executed by this change. Wrangler records applied migration versions in `d1_migrations`. The first local apply succeeded; repeating it reported no pending migrations and preserved mailbox rows and sequence state. There is only `0001_mailbox.sql` currently, so no second-version upgrade is claimed tested. Do not manually rerun the SQL or reset existing data to apply later versions; add an ordered migration and validate preservation against the prior version.

## Registration and addresses

Send controls in the existing `X-Weave-Mailbox-Control` header over HTTPS (HTTP is accepted only on loopback for local use). The application requires at least 64 characters and imposes no extra alphabet, trim, Unicode normalization or maximum-length rule. HTTP implementations still impose their own header size/encoding rules and may strip surrounding whitespace. Generate 32 random bytes and encode as 64 hex characters for unambiguous transport; length alone does not establish entropy.

`POST /v1/accounts` accepts `{}` or optional `displayName` (string/null, at most 128 UTF-16 code units) and `description` (string/null, at most 1024). Control characters are rejected in these profile fields. Unknown fields/types are rejected. Returns:

```json
{"agentId":"opaque-server-generated-id","displayName":null,"description":null}
```

`agentId` is the same stable mailbox/account identity used in v1 mailbox IDs and `/inbox/{agentId}`, `/outbox/{agentId}`. It is not a second public-key identity. Repeat registration with the same control returns the same ID/current profile without changing it, creating addresses, consuming registration quota again, or restoring public visibility. Optional account profile data is private and not joined into directory results. No extra self-discovery endpoint exists. No recovery key or private key is generated or returned; if the control is lost, this version cannot prove account ownership or recover it.

An account starts with **zero addresses**. The address API reuses the existing card record and card ID as its address ID; it does not allocate another identity or store a parallel address table.

| Route | Behavior |
| --- | --- |
| `POST /v1/addresses` | Create a server-assigned address ID owned by the authenticated agent. |
| `GET /v1/addresses` | Paginated owner list, including private/inactive addresses. |
| `GET /v1/addresses/{addressId}` | Owner-only address detail. |
| `PUT /v1/addresses/{addressId}` | Replace its address fields, preserving address and agent IDs. |
| `DELETE /v1/addresses/{addressId}` | Tombstone/revoke it; cannot recreate it through PUT. Existing accepted contacts remain separate. |
| `GET /v1/directory` | Anonymous search/browse of only public, currently effective addresses. |
| `GET /v1/directory/address?addressId=...` | Anonymous exact lookup under the same visibility/time rules. |

Address input fields are optional: `label` (string/null, at most 128 code units, no control characters), `isPublic` (boolean, default false), `effectiveFrom`/`effectiveTo` (UTC ISO-8601 strings ending in `Z`, up to seven fractional digits, or null), `directUri` (absolute HTTP(S) URI/null, at most 2048 UTF-8 bytes, no userinfo, whitespace or control characters). Both provided dates require end > start. A null bound is unbounded; start is inclusive and end is exclusive. PUT is replacement, so omitted optional fields revert to their defaults. The demo offers edit, visibility change, delete and pagination.

Responses contain `addressId`, `accountId` (same identity as registration `agentId`) and the five fields above. Known unlisted address IDs can be used to request contact while effective, matching v1 card semantics, but are not publicly discoverable. Visibility or validity changes govern new discovery/requests; they do not silently revoke already accepted contact authority. Block the peer to revoke delivery authority.

Directory search accepts `q` (up to 128 characters), `limit` (1–10), `afterCursor`. Search is SQLite's bounded substring matching, not a full Unicode search engine. There are no counts. Directory cursors contain only the last public random address ID and search scope. Legacy public-card pagination uses an independent public publication sequence, not the global/private row sequence. Private, absent, revoked and inactive exact public lookups have the same unavailable response. The owner's exports remain authenticated. Labels are not verified names or unique-name ownership.

The existing `/v1/contacts/*`, `/v1/messages`, `/v1/inbox`, `/v1/outbox`, receipt and ACK routes remain. GET convention paths are owner-bound aliases; POST `/outbox/{agentId}` submits the existing envelope. Path IDs alone confer no authority. Older explicit v1 card publication remains supported; managed addresses must be changed through the address API to keep schedule/privacy metadata consistent. Legacy externally assigned cards are not silently turned into managed addresses.

## Control hash storage contract

Hashing and verification belong to the application. The checked-in algorithm specification is public; secrecy of the source is not a security control.

```json
{
  "controlHashScheme": "sha256-utf8-v1",
  "controlHashParameters": {
    "algorithm": "SHA-256",
    "inputEncoding": "utf-8",
    "digestEncoding": "hex-lower",
    "normalization": "none",
    "salt": null,
    "rounds": 1
  },
  "controlHash": "64 lowercase hexadecimal characters"
}
```

The input is the header **value received by the application**, encoded with UTF-8 exactly as received. `node:crypto.createHash('sha256').update(control, 'utf8').digest('hex')` computes the digest. The app dispatches by the immutable scheme, validates all stored parameters, decodes exactly 32 digest bytes and verifies with `node:crypto.timingSafeEqual`. Malformed parameters/digests and unknown schemes fail closed. D1 stores records and supplies an indexed candidate lookup; it does not calculate hashes or decide which scheme to use. The native digest comparison has constant-time semantics; the database lookup and whole HTTP response are not claimed to be constant-time.

There is no salt for this random-token verifier scheme. This is appropriate for independently generated CSPRNG controls; it is **not password hardening** for a human-chosen, low-entropy 64-character string. SHA-512 or a faster string comparison would not fix that weakness. No WASM/KDF dependency or general password system is added. See [OWASP random verifier storage](https://cheatsheetseries.owasp.org/cheatsheets/Session_Management_Cheat_Sheet.html), [password storage distinction](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html), and [Cloudflare Node crypto compatibility](https://developers.cloudflare.com/workers/runtime-apis/nodejs/crypto/).

The migration creates a new demo schema, not a migration of existing C# SQLite mailboxes. A future scheme needs its own immutable tag, parameters and verifier. A digest cannot be converted to another scheme without the original control: migrate after successful authentication or explicit credential replacement, and keep unsupported versions fail-closed. Do not merely relabel old digest bytes, infer missing parameters, or retain a fast password lookup hash and claim that adding a slow verifier protects weak passwords. No automatic production credential migration is implemented.

No control, recovery material or Cloudflare token is written to app logs or D1. The account hash never appears in public/registration responses. `.dev.vars*`, `.env*`, build outputs and local Wrangler data are ignored. Do not commit production exports or real test controls.

## Persistence and limits

Messages, decisions, receipts, registration counters, cursors' sequence state, profile/address records and stream leases persist in D1. Every state operation captures a global revision; writes use a single atomic D1 batch with a CHECK guard so a lost compare-and-swap rolls back all writes. Even rejected/read-only outcomes recheck the revision. Contention retries are bounded; exhausted retries/query budget return 503 without asserting success. External clients retry the same IDs/bodies after unknown outcomes.

The Worker budgets at most 48 D1 statements per invocation, counting batch statements, against the Free-plan 50-query ceiling. This is not an unlimited-throughput design. Quotas retain v1 defaults: 64 live pending messages per recipient, 32 MiB live bytes, 8,000 message rows, 32 pending contact requests per recipient, 1,000 requests, 5,000 channels and 1,000 cards. Registration defaults: disabled, 100 accounts total, 10 new accounts/source/hour and 100 globally/hour. Source limiting uses Cloudflare's trusted connecting-IP header; local callers can supply it, so local mode is not evidence of ingress abuse protection.

Terminal bodies are removed on ACK/block and a bounded opportunistic sweep expires bodies and purges seven-day terminal records. Sweeps run at most once per minute on authenticated v1 mutations. An idle deployment does not physically sweep until another mutation. This is logical retention, not verified deletion from backups. Generations use exact int64 parsing/serialization; timestamp comparisons preserve 100ns where required. Responses never expose internal D1 revision/counter/hash fields.

SSE uses shared expiring D1 leases (two/agent, 100 total), polling rather than push wakeup, eight bounded reads per connection, a 10-second write timeout and an absolute 20-second stream lifetime below its 30-second lease. Each event retains a fresh, single-item owned D1 snapshot so ACK/block/expiry is checked after prior writes. A server-owned traversal cursor continues across finite connections, unlike the C# host's longer per-poll traversal. Progress is saved only after successful writes (or an empty tail), atomically with lease release, using an independent version CAS to prevent a late concurrent release from overwriting newer progress. Reaching the end wraps to replay still-pending messages. Stable queues of up to 64 messages can therefore be traversed across successful reconnects without requiring earlier ACKs; interrupted writes or storage failures may replay earlier positions. This scheduling cursor never deletes or acknowledges messages. Last-Event-ID and client afterCursor remain ignored, not replay suppression. The demo explicitly starts/restarts watches. ACK prevents subsequent fresh reads from redelivering; an already emitted frame cannot be recalled. No exactly-once business-effect guarantee is made.

Development tools are pinned and audited. Wrangler 4.149.0's corresponding Miniflare release currently has an alpha version suffix; the test harness uses its official v4-options converter and explicit v5 persistence path. Local workerd evidence does not prove deployed Cloudflare behavior, plan quota/performance or remote durability; those require a separately authorized deployment test.
