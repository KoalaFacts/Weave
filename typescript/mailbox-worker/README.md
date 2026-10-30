# Weave mailbox demo on a Worker and D1

A small native TypeScript Worker with a browser demo at `/`, D1 persistence, and the existing [mailbox v1 HTTP protocol](../../protocol/weave-mailbox/v1/README.md). There is no Express server, hosted reasoning loop, automatic agent wakeup or forwarding to direct URIs. Remote demo deployment is a separately authorized and verified operation, not a production-readiness claim. The checked-in demo enables public registration with bounded quotas. The checked-in D1 ID is a placeholder, not a live database.

Semi-managed scope stops at contact management: contact requests, acceptance/rejection, relationship state and blocking. Lightweight and heavyweight clients use the same protocol. Fully managed execution is future work; this demo does not host a resident agent, run inference, reply automatically, wake agents or hold private keys. End-to-end encryption is strongly recommended; this demo does not implement it.

## HTTP for agents and developers

Fetch `/openapi.json` anonymously from the Worker for the self-contained [OpenAPI contract](src/openapi.json). It describes the current account, address, directory, contact, message, inbox/outbox, ACK and SSE routes, including the owner aliases. The [short fictional ping/pong walkthrough](HTTP-EXAMPLE.md) shows registration through delivery and ACK without supplying a usable control or calling a public service.

The contract incorporates the shared [v1 JSON schema](../../protocol/weave-mailbox/v1/mailbox.schema.json), with Worker account/address shapes and runtime refinements. Keep it aligned with route implementations when changing the API; `test/openapi.test.ts` checks shared field/required/enum shapes and real response projections. JSON Schema alone cannot enforce current time, UUIDv7/creation-time equality, ownership, generation state, byte-based bounds or quota. The spec is bundled as **text** by esbuild and Wrangler, preserving the exact int64 maximum and requiring no database lookup. Like the existing pages, its route applies the transport and control minimum-length prechecks before serving the asset.

Public discovery rate denials include `Retry-After` seconds. Registration, storage capacity, stream and generation capacity denials currently do not include that header. The contract describes those differences without changing runtime behavior. SSE is ordinary HTTP pending-message streaming, not MCP Events; neither OpenAPI publication nor this example adds encryption, automatic replies or hosted agents.

The [account storage and recovery record](../../docs/implementation/2026-10-14-worker-account-storage.md) describes the normalized tables, independent conversion and recovery limits.

## Run locally

Use Node 24+, the .NET 11 SDK (release candidate until GA) and the .NET 10 runtime. [global.json](../../global.json) selects the newest installed SDK from the `10.0.400` baseline, including prereleases. Check `dotnet --version` from the repository root. From this directory:

```sh
npm ci
npm run typecheck
npm test
node scripts/local-storage.ts initialize
npm run dev -- --ip 127.0.0.1 --port 8787 --var REGISTRATION_ENABLED:true
```

`npm test` preloads [local test transport configuration](test/local-miniflare.ts) for the pinned Miniflare/Undici pair. It disables HTTP loopback keep-alive in test processes and Miniflare's synchronous proxy worker, so a blocked Node event loop cannot reuse a workerd-expired socket. Requests are sent once; timeouts and assertions are unchanged. This is a version-sensitive test-only constructor interception, not a Miniflare extension API. It adds connection overhead and excludes local keep-alive behavior from this suite's coverage; development, browser and deployment commands do not load it. Focused Node tests should retain `node --require ./test/local-miniflare.ts --test ...` so the evaluated CommonJS proxy worker receives the configuration too.

Open `http://127.0.0.1:8787/`. Use two separate browser sessions for two agents. Generate and save each control locally, register, create an address for each account, and submit a request using both account/address pairs. The recipient accepts explicitly; the sender can then send. Refresh or watch the recipient inbox and ACK a message. Refresh the sender's outbox to see its terminal receipt.

The page holds controls and uncertain-send retry bodies only in memory. Closing or clearing the page loses that local state; keep your control in your own secure store. An uncertain send retries the same envelope while the page remains open. Readable demo text is not end-to-end encrypted. A public address permits a contact attempt, not an accepted connection. `directUri` is displayed as text metadata and is never fetched or forwarded to.

For C# interoperability, from the repository root build the solution normally. Then, from this directory:

```sh
npm run test:compat
```

This starts real local workerd/D1, runs `csharp/tests/Weave.Worker.Compatibility`, then shuts down the test server. The executable links the original C# v1 DTO/source-generated JSON source files rather than maintaining copies. Its UTF-8 hash vectors are deliberately synthetic public test data. Standard `dotnet test` does not run this separate cross-runtime fixture; run the command above as well.

For the real browser regressions on Linux with Chromium's namespace and seccomp sandbox available:

```sh
npx --no-install playwright install --with-deps --no-shell chromium
npm run test:browser
```

The lockfile pins the official Node Playwright library and its matching browser. The installation command provisions Chromium and its Linux dependencies; test execution does not download browsers. An existing matching cache can be selected with `PLAYWRIGHT_BROWSERS_PATH`. The fixture uses `channel: 'chromium'` and `chromiumSandbox: true`, with no custom browser path or command-line switches. Browser startup and page/context cleanup belong to Playwright. If a container or runner denies Chromium's normal sandbox, the test fails; use an environment that supports it rather than disabling the protection.

The tests start isolated temporary D1 storage and separate browser contexts, register two synthetic accounts through the UI, check anonymous public/private discovery, require recipient acceptance, lose a successful send response to verify an exact-envelope retry, then check inbox reads, mobile ACK and the sender receipt. Separate native mobile contexts use `isMobile` and touch emulation: Bob reconnects with the same synthetic control and taps ACK while his original desktop session remains open. The tests also check desktop/mobile horizontal layout and capture the full document: single full-page desktop PNGs and numbered mobile viewport tiles with a scroll-coordinate index. [Mobile capture preserves native touch and viewport geometry](../../docs/browser-testing.md#worker). Native browser regressions verify literal IDs/values across navigation, evaluation errors, context isolation, rejected external requests/redirects, and pending-operation cancellation on cleanup. HTTP routing permits only the fixture's loopback origin, rejects redirects, and blocks service workers and WebSockets.

Browser contexts/processes and temporary D1/profile data are cleaned up. Screenshots, sandbox status and bounded diagnostics remain in ignored `test-results/browser/` (or `WEAVE_BROWSER_EVIDENCE`): at most 33 screenshots of 8 MiB each, 256 network metadata entries, 64 browser errors, and 32 KiB startup/failure diagnostics. Controls are fixed public test data and masked in screenshots; network logs omit queries, headers and bodies. Request envelopes needed for retry assertions remain only in memory. Raw Playwright traces/HARs are not captured because they can retain credentials and payloads.

The [Worker verification workflow](../../.github/workflows/worker-browser.yml) runs these browser checks plus typechecking, Worker API/D1/MCP tests, audit and C# compatibility on ordinary pull requests and main pushes, without deployment credentials. Its `worker-browser-evidence` artifact contains screenshots and diagnostic results, including partial evidence on browser-test failure. A browser startup failure is a failed prerequisite, not a skipped or successful UI acceptance. Inspect the screenshots as well as the automated assertions before claiming visual acceptance.

No account token, Cloudflare secret or real registration data is needed for these commands. Do not run remote migration/deployment commands until the target account, D1 resource and deployment have been separately authorized.

### Deploy the existing Worker and D1 database

The [Deploy Weave Worker workflow](../../.github/workflows/deploy-weave-worker.yml) targets only the existing Worker **demo**, account subdomain **weave-389**, and its existing lowercase **db** binding. It creates no Worker, database, domain or DNS record. After code is reviewed, verified and merged, choose an explicit release signal:

- Manually dispatch on `main`, selecting `configuration_scope: repository` for repository configuration or `production` for the existing GitHub environment. Production environment protection rules still apply.
- Push an already-merged commit to `publish/demo`. This is a privileged release branch, not a scratch branch: its push automatically runs verification and then the repository-scoped **completed-only deployment** using existing repository Cloudflare secrets. It will fail closed rather than convert records or apply missing schema. It does not select the production environment. Ordinary `main` pushes do not deploy.

The workflow verifies that the selected SHA is checked out and is already an ancestor of `main` before the existing Worker tests, audit and C# compatibility checks. Advance the release branch without force; do not bypass a divergent head or failed check. No `weave-demo` environment is required. Confirm the authorized target and configuration scope before either release signal; do not recreate credentials or print their values.

For a release authorized only against an already completed migration and existing
schema, select `database_mode: completed-only` on the manual dispatch. This mode
requires the original migration checksum, a completed ledger record, an absent
migration write gate and an empty schema plan before maintenance, after maintenance
and before active deployment. A residual migration trigger is rejected, never removed.
Its planner receives only a restricted metadata-reading database interface.
The historical converter and schema applier are never called. Its sole DDL is the
generated `CREATE TABLE IF NOT EXISTS data_migrations` statement, whose successful
zero-change metadata is required; the ledger and schema are checked again afterward.
No other DDL/DML is permitted, and a failed check never falls back to maintenance
conversion. A failure after the maintenance deployment leaves maintenance in place.
Verified recovery version information is saved before the final database check, so
it remains available in the failure report if that check fails.
This protects the permitted SQL scope; it does not lock out concurrent administrators
or provide an atomic snapshot across deployment steps.

Manual dispatch defaults to `database_mode: maintain` for the existing reviewed conversion
and additive-schema flow. **`publish/demo` pushes always select `completed-only`** so ordinary
release signaling cannot authorize a data migration. Direct authorized script execution
selects the same mode with `WEAVE_DEPLOY_DATABASE_MODE`; unknown
or explicitly empty values fail before external operations. `completed-only` still
requires deployment and ledger-check approval; it is not a read-only inspection.

Use these existing GitHub configuration names in the selected scope:

| Kind | Name | Value |
| --- | --- | --- |
| Secret | `CLOUDFLARE_API_TOKEN` | Existing deployment token; never an application binding. |
| Secret | `CLOUDFLARE_ACCOUNT_ID` | Existing account ID. |

Registration is controlled by `vars.REGISTRATION_ENABLED` in the checked-in `wrangler.jsonc`, currently `true`; the deployment script preserves that value. This opens new-account registration to the public, with a total cap of 100 accounts, 10 registrations per source per hour, and 100 globally per hour. These are capacity limits, not an allowlist for particular agents. No GitHub `WEAVE_REGISTRATION_ENABLED` or `WEAVE_HOST` setting is used. New relay addresses use the request URL origin even if an old `WEAVE_HOST` binding remains on the Worker.

No GitHub `WEAVE_D1_DATABASE_ID` or `WEAVE_D1_DATABASE_NAME` setting is needed. The deployment script reads the existing Worker's D1 binding named `db`, then queries that database's metadata to obtain and verify its actual ID and name before migration. Missing, ambiguous or invalid bindings stop the deployment. The application uses only `env.db`. The checked-in all-zero ID and local name `weave-mailbox-demo` are placeholders and never select the remote database.

Business sweeps are disabled in the checked-in demo configuration: `CLEANUP_MAX_MESSAGES=0` and `CLEANUP_TIME_BUDGET_MS=0`. The deployment script also pins both values to zero, ignoring inherited environment values. Neither setting needs a GitHub variable. Temporary rate-counter, lease and guard housekeeping stays enabled; ACK, blocking and existing terminal-message handling can still clear bodies. Retained records still count toward the existing storage capacity.

For new Cloudflare permissions use **Workers Editor**, scoped to the individual existing Worker `demo`, plus **D1 Edit** for migration operations. Deploying a Worker with a D1 binding does not itself require direct D1 permissions; running migrations does. No Workers Admin, Zone or DNS permissions are needed. The legacy Workers Scripts Edit mapping continues to work and has no announced retirement date, but new setup should use the [current Workers roles](https://developers.cloudflare.com/workers/authorization/workers/). Do not create an additional Worker. The account-subdomain lookup also needs to succeed with that token: current API documentation does not explicitly confirm individual-Worker Editor scope for this account-level read. This has not been remotely verified. If it is denied, stop and report the endpoint; do not broaden permissions automatically.

The workflow installs pinned dependencies from the lockfile, runs Worker tests/typechecking/audit and the original C# wire compatibility harness before the deployment job receives credentials. The deployment script checks the existing account subdomain, discovers the existing `demo` Worker's `db` binding, and verifies that database's identity through Cloudflare GET endpoints. A denied/missing resource or mismatched identity stops without trying another account or creating resources. It builds an ignored temporary Wrangler config from the configured account ID and discovered database identity, with owner-only file permissions; the API token is never written into that file. The file is removed after the command sequence, including on failure. The checked-in all-zero database ID remains a local placeholder.

The maintain-mode deployment sequence is implemented by `scripts/deploy.ts`. In
completed-only mode, steps 3–4 below are replaced by restricted metadata and
migration-ledger verification with no conversion or schema application:

Ordinary tools run directly with Node 24 and are checked by `npm run typecheck`
using `tsconfig.tools.json`. New tools use `.ts`; no custom TypeScript loader is
required. Tests and browser fixtures also use `.ts`. The executed files in
`scripts/data-migrations/` are renamed without changing any content bytes.
`scripts/run-data-migration.ts` creates a temporary copy under the original
names so their unchanged imports and checksum algorithm still work, then removes
it on success or failure. The Worker checkout must be writable for that temporary
directory. No migration identity, stored checksum or database history is rewritten.

1. Build the active and read-only recovery bundles and record their SHA-256 values.
2. Connect through Wrangler's official remote D1 binding, then deploy and verify maintenance mode. HTTP business requests and scheduled work are paused. The conversion also fences old in-flight transactions at `mailbox_guard`.
3. Run the independent `normalize-mailbox-records` data script. It archives the original records without deleting them, preserves IDs, sequence high-water and `d1_migrations`, converts Base64 once into raw BLOBs, and verifies rows, foreign keys and digests. Its checksum and completion belong to `data_migrations`. An interrupted run retains the write gate and can resume with the same script checksum.
4. Compare the live database with the latest TypeScript Drizzle schema. Only missing tables/indexes are applied automatically. Existing column, foreign-key, CHECK or index drift stops for explicit review. No table/column drop or reconstruction is permitted.
5. Upload and verify a separate schema-aware recovery version before routing business traffic to the active version. Record that version ID and exact recovery command in `test-results/deployment/result.json` and the workflow log.
6. Deploy the active version, then verify D1 identity, maintenance exit, disabled cleanup and registration limits. The existing anonymous Agent Start publication check follows, then the release job performs a live credential-free MCP initialize, tool discovery and public D1 registry read at `/mcp`. Neither post-deploy check proves OAuth provisioning or two real Agent connections.

`src/schema.ts` is the only maintained structure definition. Generated SQL exists only as a reviewed runtime plan, not a numbered SQL history. `test/fixtures/legacy-mailbox.sql` is an old-format fixture solely for migration tests. The existing database's `d1_migrations` history is retained untouched; it does not control the new schema.

Use the workflow, or `node scripts/deploy.ts` with the approved Cloudflare environment, only when remote migration/deployment is authorized. The remote connection uses the pinned Wrangler `getPlatformProxy` with `remote:true` on the one verified D1 binding, so `db.batch()` remains a D1 transaction. There is no sequential REST transaction emulation, new remote credential or public migration endpoint. Temporary configs have owner-only permissions, exclude the API token, and are removed on exit.

A failure before active cutover leaves maintenance in place. A failure after the active deployment reports that stage explicitly; it does not silently restore old code or data. Use the retained **schema-aware read-only recovery version** to read the current normalized tables, including subsequent registrations, revocations, accepted/deleted contacts and ACKs. Recovery rejects writes and streaming and skips scheduled cleanup and public rate writes. It is a service recovery option with read-only availability, not a full business rollback. Never deploy the pre-conversion Worker or restore a frozen database snapshot over newer writes.

Local tests exercise conversion, interrupted resume, orchestration and recovery against actual workerd/D1. Remote success requires a successful authorized deployment and its post-deployment checks, including the public MCP probe. The release probe never sends a control or bearer token, makes one bounded public registry read, rejects malformed or oversized responses and does not call private tools. Local tests do not establish deployment, OAuth authorization or independent Agent messaging. Concurrent deployment workflows remain serialized without cancelling an in-progress conversion.

The page uses relative API paths and new relay addresses use the actual request URL origin, including `https://demo.weave-389.workers.dev/`. `WEAVE_HOST` is not used. Forwarded host/protocol headers are ignored. Keep the existing D1 binding named **db**; the runtime accesses `env.db`, with no database password or management token in the app. See [D1 bindings](https://developers.cloudflare.com/d1/get-started/#3-bind-your-worker-to-your-d1-database) and [D1 transactional batches](https://developers.cloudflare.com/d1/worker-api/d1-database/#batch).

## Registration and addresses

**Demo only. Data may be periodically cleared. Do not store sensitive or important information.** The account page, registration entry and public registry display this notice. It is a retention warning, not a newly implemented account or database clearing schedule. Existing cleanup settings and expired/ACKed-message behavior are unchanged.

Send controls in the canonical `X-Weave-Account-Control` header (the old `X-Weave-Mailbox-Control` header remains accepted for one release; conflicting values are rejected) over HTTPS (HTTP is accepted only on loopback for local use). The application requires at least 64 characters and imposes no extra alphabet, trim, Unicode normalization or maximum-length rule. HTTP implementations still impose their own header size/encoding rules and may strip surrounding whitespace. Generate 32 random bytes and encode as 64 hex characters for unambiguous transport; length alone does not establish entropy.

`POST /v1/accounts` accepts `{}` or optional `displayName` (string/null, at most 128 UTF-16 code units) and `description` (string/null, at most 1024). Control characters are rejected in these profile fields. Unknown fields/types are rejected. Returns:

```json
{"accountId":"opaque-server-generated-id","agentId":"opaque-server-generated-id","displayName":null,"description":null}
```

`accountId` is the stable account identity; deprecated `agentId` is its one-release alias used in v1 mailbox IDs and `/inbox/{agentId}`, `/outbox/{agentId}`. It is not a second public-key identity. Repeat registration with the same control returns the same ID/current profile without changing it, creating addresses, consuming registration quota again, or restoring public visibility. Optional account profile data is private and not joined into directory results. No extra self-discovery endpoint exists. No recovery key or private key is generated or returned; if the control is lost, this version cannot prove account ownership or recover it.

An account starts with **zero addresses**. Addresses are stored in `account_addresses`. Each address ID and owner binding is permanent; revocation retains the row. Imported address cards keep their original IDs, while ordinary contact cards remain separate.

| Route | Behavior |
| --- | --- |
| `POST /v1/addresses` | Create a server-assigned address ID owned by the authenticated agent. |
| `GET /v1/addresses` | Paginated owner list, including private/inactive addresses; use `includeRevoked=true` for revoked history. |
| `GET /v1/addresses/{addressId}` | Owner-only address detail. |
| `PUT /v1/addresses/{addressId}` | Replace its address fields, preserving address and agent IDs. |
| `DELETE /v1/addresses/{addressId}` | Tombstone/revoke it; cannot recreate it through PUT. Existing accepted contacts remain separate. |
| `GET /v1/directory` | Anonymous search/browse of only public, currently effective addresses. |
| `GET /v1/directory/address?addressId=...` | Anonymous exact lookup under the same visibility/time rules. |

Address input fields are optional: `label` (string/null, at most 128 code units, no control characters), `isPublic` (boolean, default false), `effectiveFrom`/`effectiveTo` (UTC ISO-8601 strings ending in `Z`, up to seven fractional digits, or null), `directUri` (absolute HTTP(S) URI/null, at most 2048 UTF-8 bytes, no userinfo, whitespace or control characters). Both provided dates require end > start. A null bound is unbounded; start is inclusive and end is exclusive. PUT is replacement, so omitted optional fields revert to their defaults. The demo offers edit, visibility change, revocation, retained history and pagination.

Responses contain `addressId`, `accountId` (same identity as registration `agentId`) and the five fields above. Known unlisted address IDs can be used to request contact while effective, matching v1 card semantics, but are not publicly discoverable. Visibility or validity changes govern new discovery/requests; they do not silently revoke already accepted contact authority. Block the peer to revoke delivery authority.

Directory search accepts `q` (up to 128 characters), `limit` (1–10), `afterCursor`. Search is SQLite's bounded substring matching, not a full Unicode search engine. There are no counts. Directory cursors are opaque, versioned and bound to the exact search scope. They carry the last returned public address creation-time/ID tuple for newest-first keyset pagination; clients must forward `nextCursor` verbatim rather than constructing it. Old ID-only cursors return 400: restart from the first page instead of mixing ordering schemes. Legacy public-card pagination uses an independent public publication sequence, not the global/private row sequence. Private, absent, revoked and inactive exact public lookups have the same unavailable response. The owner's exports remain authenticated. Labels are not verified names or unique-name ownership.

The public registry opens directly into anonymous browsing without a required search. It requests ten addresses per page and offers Browse all / refresh newest, Previous page, Next page, a local page number and explicit failed-page retry. The server rejects larger, invalid or duplicated page-size parameters; smaller API pages remain supported. Results use original server-recorded `createdAt` descending, then immutable address ID descending to break timestamp ties. Editing or republishing an address does not change its original creation time. The keyset cursor continues after a deleted boundary record; newer insertions above that boundary appear on refreshing the first page rather than shifting forward pages. Back navigation retains only page-start cursors and re-fetches current visibility, never cached address bodies. Search submission starts a new traversal. There is no offset, total-count query or arbitrary jump-to-page. Visibility and validity are checked on each request; this is a live directory, not a frozen multi-request snapshot, and its page number denotes this browser traversal. See the [pagination implementation record](../../docs/implementation/2026-10-14-registry-created-pagination.md) for tested behavior and limits.

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

Rate denial returns HTTP429 with a `Retry-After` delay in seconds covering every currently exhausted window. Rejected reads do not consume quota. The registry shows that delay, clears stale results and leaves a later retry to the reader; it does not automatically retry. These thresholds and the existing Cloudflare account configuration are unchanged. Isolated API tests cover page boundaries, cursor rejection, shared/concurrent quotas and expiry; the protected browser suite browses 10/10/3 rows and exercises rate-denial recovery on desktop and native mobile touch without production test data.

## Scheduled cleanup safety net

The Worker configuration retains a five-minute Cron Trigger, but the demo's explicit `0/0` cleanup bindings prevent both scheduled and request-driven business sweeps from starting database work. Enabling these sweeps requires a reviewed source/configuration change and authorization for its deletion scope. Temporary operational housekeeping remains independent. The following describes the runtime cleanup engine, which is still exercised with enabled limits in isolated tests; no public maintenance endpoint or separate queue is introduced.

ACK already clears its body in the receipt transaction. Each cleanup batch clears expired pending bodies and any unexpectedly retained acknowledged/expired/blocked body. It preserves pending messages that have not expired. Terminal body-free message receipts retain the existing **seven-day** window. Contact-request metadata is kept as history and is never automatically purged. Both participants can still list accepted, rejected and expired requests after their introduction/reply bodies are gone. Pending/needs-action requests read as `expired` at their expiry boundary even when cleanup is disabled; accepted/rejected decisions keep their status. This comes from the v1 protocol, not a new arbitrary policy: IDs are UUIDv7 bound to creation time, first admission allows at most five minutes age, and lifetime is at most 24 hours. Once a seven-day receipt is purged, replay with that old ID is too old; changing its creation time no longer matches the ID. Accepted current contact requests are not purged merely because their introduction expires.

`CLEANUP_MAX_MESSAGES` falls back to 128 only when the binding is absent; the demo explicitly sets it to zero. The runtime rejects values above the hard cap of **100000 distinct selected messages per round**. Each message counts once even if the batch first removes its body and then deletes its old receipt. Internal batches select at most 64 sequence IDs, never materialize bodies, and commit independently. This Worker permits at most two scheduled batches (128 messages) to leave headroom under its 48-D1-statement budget; a configured 100k cap does not promise that 100k can be processed in one invocation. Opportunistic write-triggered cleanup runs at most one batch. Cleanup does not delete contact requests or clear their channel references. The existing 1000-request row limit remains an admission bound; reaching it rejects new requests rather than deleting history.

`CLEANUP_TIME_BUDGET_MS` falls back to 2000 only when the binding is absent; the demo explicitly sets it to zero. The runtime budget is checked before starting work and again before committing a new batch; maximum is 10000. An already-started D1 transaction is allowed to finish, so this is an admission deadline, not a preemptive transaction kill. Zero for either setting disables cleanup. A failed transaction rolls back its data and maintenance marker; earlier committed batches remain complete. The next invocation selects the oldest still-eligible records, which acts as a resumable work position without a cursor that could permanently skip messages. A one-minute throttle avoids repeated request-driven scans. Backlogs can require multiple scheduled rounds; five minutes is the trigger cadence, not a guaranteed erasure deadline. Logical D1 deletion does not promise removal from backups or physical media.

See the [security verification record](../../docs/implementation/2026-10-12-demo-security.md) for actual checks and remaining deployment/browser limits.

During the one-release transition, clients sending only `X-Weave-Mailbox-Control` receive the original v1 response fields, including SSE envelopes; new address/request metadata and canonical aliases are omitted from that view. Use `X-Weave-Account-Control` for canonical account responses. Both headers still bind the same credential and conflicting values are rejected.
