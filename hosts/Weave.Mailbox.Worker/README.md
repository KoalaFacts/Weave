# Weave mailbox demo on a Worker and D1

A small native TypeScript Worker with a browser demo at `/`, D1 persistence, and the existing [mailbox v1 HTTP protocol](../../protocol/weave-mailbox/v1/README.md). There is no Express server, hosted reasoning loop, automatic agent wakeup, forwarding to direct URIs, or production deployment in this change. Registration is disabled by default. The checked-in D1 ID is a placeholder, not a live database.

Semi-managed scope stops at contact management: contact requests, acceptance/rejection, relationship state and blocking. Lightweight and heavyweight clients use the same protocol. Fully managed execution is future work; this demo does not host a resident agent, run inference, reply automatically, wake agents or hold private keys. End-to-end encryption is strongly recommended; this demo does not implement it.

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

For the real browser regressions on Linux with Chromium's namespace and seccomp sandbox available:

```sh
npx --no-install playwright install --with-deps --no-shell chromium
npm run test:browser
```

The lockfile pins the official Node Playwright library and its matching browser. The installation command provisions Chromium and its Linux dependencies; test execution does not download browsers. An existing matching cache can be selected with `PLAYWRIGHT_BROWSERS_PATH`. The fixture uses `channel: 'chromium'` and `chromiumSandbox: true`, with no custom browser path or command-line switches. Browser startup and page/context cleanup belong to Playwright. If a container or runner denies Chromium's normal sandbox, the test fails; use an environment that supports it rather than disabling the protection.

The tests start isolated temporary D1 storage and separate browser contexts, register two synthetic accounts through the UI, check anonymous public/private discovery, require recipient acceptance, lose a successful send response to verify an exact-envelope retry, then check inbox reads, mobile ACK and the sender receipt. Separate native mobile contexts use `isMobile` and touch emulation: Bob reconnects with the same synthetic control and taps ACK while his original desktop session remains open. The tests also check desktop/mobile horizontal layout and capture full-page screenshots. Native browser regressions verify literal IDs/values across navigation, evaluation errors, context isolation, rejected external requests/redirects, and pending-operation cancellation on cleanup. HTTP routing permits only the fixture's loopback origin, rejects redirects, and blocks service workers and WebSockets.

Browser contexts/processes and temporary D1/profile data are cleaned up. Screenshots, sandbox status and bounded diagnostics remain in ignored `test-results/browser/` (or `WEAVE_BROWSER_EVIDENCE`): at most 32 screenshots of 8 MiB each, 256 network metadata entries, 64 browser errors, and 32 KiB startup/failure diagnostics. Controls are fixed public test data and masked in screenshots; network logs omit queries, headers and bodies. Request envelopes needed for retry assertions remain only in memory. Raw Playwright traces/HARs are not captured because they can retain credentials and payloads.

The [Worker verification workflow](../../.github/workflows/worker-browser.yml) runs these browser checks plus typechecking, Worker API/D1/MCP tests, audit and C# compatibility on ordinary pull requests and main pushes, without deployment credentials. Its `worker-browser-evidence` artifact contains screenshots and diagnostic results, including partial evidence on browser-test failure. A browser startup failure is a failed prerequisite, not a skipped or successful UI acceptance. Inspect the screenshots as well as the automated assertions before claiming visual acceptance.

No account token, Cloudflare secret or real registration data is needed for these commands. Do not run remote migration/deployment commands until the target account, D1 resource and deployment have been separately authorized.

### Deploy the existing Worker and D1 database

The manual [Deploy Weave Worker workflow](../../.github/workflows/deploy-weave-worker.yml) targets only the existing Worker **demo**, account subdomain **weave-389**, and its existing lowercase **db** binding. It creates no Worker, database, domain or DNS record. After this draft is reviewed and merged, dispatch the workflow on `main`, selecting `configuration_scope: repository` for repository configuration or `production` for the existing GitHub environment. No `weave-demo` environment is required. Environment protection rules still apply. Confirm where the already-prepared configuration is saved before dispatching; do not recreate credentials or print their values.

Use these existing GitHub configuration names in the selected scope:

| Kind | Name | Value |
| --- | --- | --- |
| Secret | `CLOUDFLARE_API_TOKEN` | Existing deployment token; never an application binding. |
| Secret | `CLOUDFLARE_ACCOUNT_ID` | Existing account ID. |

Registration is controlled by `vars.REGISTRATION_ENABLED` in the checked-in `wrangler.jsonc`, currently `false`; the deployment script preserves that value. No GitHub `WEAVE_REGISTRATION_ENABLED` or `WEAVE_HOST` setting is used. New relay addresses use the request URL origin even if an old `WEAVE_HOST` binding remains on the Worker.

No GitHub `WEAVE_D1_DATABASE_ID` or `WEAVE_D1_DATABASE_NAME` setting is needed. The deployment script reads the existing Worker's D1 binding named `db`, then queries that database's metadata to obtain and verify its actual ID and name before migration. Missing, ambiguous or invalid bindings stop the deployment. The application uses only `env.db`. The checked-in all-zero ID and local name `weave-mailbox-demo` are placeholders and never select the remote database.

Business sweeps are disabled in the checked-in demo configuration: `CLEANUP_MAX_MESSAGES=0` and `CLEANUP_TIME_BUDGET_MS=0`. The deployment script also pins both values to zero, ignoring inherited environment values. Neither setting needs a GitHub variable. Temporary rate-counter, lease and guard housekeeping stays enabled; ACK, blocking and existing terminal-message handling can still clear bodies. Retained records still count toward the existing storage capacity.

For new Cloudflare permissions use **Workers Editor**, scoped to the individual existing Worker `demo`, plus **D1 Edit** for migration operations. Deploying a Worker with a D1 binding does not itself require direct D1 permissions; running migrations does. No Workers Admin, Zone or DNS permissions are needed. The legacy Workers Scripts Edit mapping continues to work and has no announced retirement date, but new setup should use the [current Workers roles](https://developers.cloudflare.com/workers/authorization/workers/). Do not create an additional Worker. The account-subdomain lookup also needs to succeed with that token: current API documentation does not explicitly confirm individual-Worker Editor scope for this account-level read. This has not been remotely verified. If it is denied, stop and report the endpoint; do not broaden permissions automatically.

The workflow installs pinned dependencies from the lockfile, runs Worker tests/typechecking/audit and the original C# wire compatibility harness before the deployment job receives credentials. The deployment script checks the existing account subdomain, discovers the existing `demo` Worker's `db` binding, and verifies that database's identity through Cloudflare GET endpoints. A denied/missing resource or mismatched identity stops without trying another account or creating resources. It builds an ignored temporary Wrangler config from the configured account ID and discovered database identity, with owner-only file permissions; the API token is never written into that file. The file is removed after the command sequence, including on failure. The checked-in all-zero database ID remains a local placeholder.

The only remote mutation sequence is:

```sh
# The script supplies the validated temporary --config to every command.
wrangler d1 migrations list db --remote
wrangler d1 migrations apply db --remote
wrangler d1 migrations list db --remote
# Continue only after the pinned CLI confirms: No migrations to apply!
wrangler deploy --keep-vars
```

Use the workflow, or `node scripts/deploy.mjs` from this directory with the same approved environment variables; do not run these commands until remote migration/deployment is authorized. The workflow uses the lockfile's local Wrangler binary, not an unpinned downloaded CLI. List may initialize Wrangler's migration metadata. Any command failure or unrecognized/pending post-apply state stops deployment. A failed deployment can leave successfully applied migrations in place: correct the deployment error and rerun the workflow. Wrangler's `d1_migrations` history makes already-applied versions a no-op. Do not manually replay SQL, edit applied migrations, add `IF NOT EXISTS` to hide schema drift, or reset business data. Future changes require new numbered migrations that remain compatible with the previously deployed Worker until rollout completes. These migrations are the sole schema authority; no parallel Drizzle/schema-sync framework exists.

After deployment, the script reads the existing Worker settings and requires both cleanup bindings to be plain-text `0`. A denied lookup, invalid response or different value fails the workflow with an explicit warning that deployment already completed; it does not retry, roll back or print all bindings. This read is the confirmation of the deployed settings, separate from passing local tests.

The first local apply executed all 13 commands; repeating it reported no pending migrations and preserved mailbox rows and sequence state. Only `0001_mailbox.sql` exists, so a second-version upgrade is not claimed tested. Remote migration/deployment has not been executed as part of this change. Concurrent workflow deployments are serialized without cancelling an in-progress migration.

The page uses relative API paths and new relay addresses use the actual request URL origin, including `https://demo.weave-389.workers.dev/`. `WEAVE_HOST` is not used. Forwarded host/protocol headers are ignored. Keep the existing D1 binding named **db**; the runtime accesses `env.db`, with no database password or management token in the app. See [D1 bindings](https://developers.cloudflare.com/d1/get-started/#3-bind-your-worker-to-your-d1-database) and [numbered migrations](https://developers.cloudflare.com/d1/reference/migrations/).

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

Terminal bodies are removed on ACK/block. The runtime also supports bounded sweeps to expire bodies and purge seven-day terminal records, but both scheduled and opportunistic business sweeps are disabled by this demo deployment profile. When explicitly enabled in another configuration, opportunistic sweeps run at most once per minute on authenticated v1 mutations and scheduled sweeps can run while idle. This is logical retention, not verified deletion from backups. Generations use exact int64 parsing/serialization; timestamp comparisons preserve 100ns where required. Responses never expose internal D1 revision/counter/hash fields.

SSE uses shared expiring D1 leases (two/agent, 100 total), polling rather than push wakeup, eight bounded reads per connection, a 10-second write timeout and an absolute 20-second stream lifetime below its 30-second lease. Each event retains a fresh, single-item owned D1 snapshot so ACK/block/expiry is checked after prior writes. A server-owned traversal cursor continues across finite connections, unlike the C# host's longer per-poll traversal. Progress is saved only after successful writes (or an empty tail), atomically with lease release, using an independent version CAS to prevent a late concurrent release from overwriting newer progress. Reaching the end wraps to replay still-pending messages. Stable queues of up to 64 messages can therefore be traversed across successful reconnects without requiring earlier ACKs; interrupted writes or storage failures may replay earlier positions. This scheduling cursor never deletes or acknowledges messages. Last-Event-ID and client afterCursor remain ignored, not replay suppression. The demo explicitly starts/restarts watches. ACK prevents subsequent fresh reads from redelivering; an already emitted frame cannot be recalled. No exactly-once business-effect guarantee is made.

Development tools are pinned and audited. Wrangler 4.149.0's corresponding Miniflare release currently has an alpha version suffix; the test harness uses its official v4-options converter and explicit v5 persistence path. Local workerd evidence does not prove deployed Cloudflare behavior, plan quota/performance or remote durability; those require a separately authorized deployment test.

## Public registry and agent MCP

`/registry` is an anonymous browser entry for current public addresses, with search, pagination and exact lookup. It calls the same `/v1/directory` API without control headers or cookies. Labels and direct-URI metadata are displayed as text, not executed or followed. The account demo links to it. Only explicitly public, currently effective, unrevoked addresses appear; private account profiles and mailbox bodies are never directory data. Public discovery is not message authority: recipients still accept contact independently.

The repository's [runnable demo MCP](../../examples/mailbox-demo-mcp/README.md) follows the existing native Node stdio example and uses these same HTTP endpoints. Tool discovery includes a guide, registration, address creation/listing, registry lookup, local payload preparation, contact requests/decisions, channel lookup, send, inbox/outbox and ACK. Startup performs no registration. The operator injects one 64+ character control per process; no control is generated or persisted by the example. Tests use real processes against local workerd/D1, never the public deployment.

Public registry and legacy card discovery share D1-backed rate counters: 120 requests per source per 60-second window, 2000 globally per window. The window starts with the first request, not at a wall-clock boundary. Only hashed source identifiers are retained and stale counters are deleted in bounded batches; these are pseudonymous counters, not a claim of irreversible anonymization. The limit applies to API requests, not static page rendering. Source identity depends on Cloudflare's trusted `CF-Connecting-IP`; local-header tests cannot prove deployed ingress trust or resistance to distributed denial of service. Query length, page size, record capacity and per-request D1 budget remain enforced.

## Scheduled cleanup safety net

The Worker configuration retains a five-minute Cron Trigger, but the demo's explicit `0/0` cleanup bindings prevent both scheduled and request-driven business sweeps from starting database work. Enabling these sweeps requires a reviewed source/configuration change and authorization for its deletion scope. Temporary operational housekeeping remains independent. The following describes the runtime cleanup engine, which is still exercised with enabled limits in isolated tests; no public maintenance endpoint or separate queue is introduced.

ACK already clears its body in the receipt transaction. Each cleanup batch clears expired pending bodies and any unexpectedly retained acknowledged/expired/blocked body. It preserves pending messages that have not expired. Terminal body-free receipts and terminal contact requests retain the existing **seven-day** window. This comes from the v1 protocol, not a new arbitrary policy: IDs are UUIDv7 bound to creation time, first admission allows at most five minutes age, and lifetime is at most 24 hours. Once a seven-day receipt is purged, replay with that old ID is too old; changing its creation time no longer matches the ID. Accepted current contact requests are not purged merely because their introduction expires.

`CLEANUP_MAX_MESSAGES` falls back to 128 only when the binding is absent; the demo explicitly sets it to zero. The runtime rejects values above the hard cap of **100000 distinct selected messages per round**. Each message counts once even if the batch first removes its body and then deletes its old receipt. Internal batches select at most 64 sequence IDs, never materialize bodies, and commit independently. This Worker permits at most two scheduled batches (128 messages) to leave headroom under its 48-D1-statement budget; a configured 100k cap does not promise that 100k can be processed in one invocation. Opportunistic write-triggered cleanup runs at most one batch. Contact-request cleanup has its separate existing 128-row bound and clears only the corresponding channel references.

`CLEANUP_TIME_BUDGET_MS` falls back to 2000 only when the binding is absent; the demo explicitly sets it to zero. The runtime budget is checked before starting work and again before committing a new batch; maximum is 10000. An already-started D1 transaction is allowed to finish, so this is an admission deadline, not a preemptive transaction kill. Zero for either setting disables cleanup. A failed transaction rolls back its data and maintenance marker; earlier committed batches remain complete. The next invocation selects the oldest still-eligible records, which acts as a resumable work position without a cursor that could permanently skip messages. A one-minute throttle avoids repeated request-driven scans. Backlogs can require multiple scheduled rounds; five minutes is the trigger cadence, not a guaranteed erasure deadline. Logical D1 deletion does not promise removal from backups or physical media.

See the [security verification record](../../docs/implementation/2026-10-12-demo-security.md) for actual checks and remaining deployment/browser limits.
