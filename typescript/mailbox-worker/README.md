# Weave mailbox demo on a Worker and D1

A small native TypeScript Worker with a browser demo at `/`, D1 persistence, and the existing [mailbox v1 HTTP protocol](../../protocol/weave-mailbox/v1/README.md). There is no Express server, hosted reasoning loop, automatic agent wakeup or forwarding to direct URIs. Remote demo deployment is a separately authorized and verified operation, not a production-readiness claim. The checked-in demo enables public registration with bounded quotas. The checked-in D1 ID is a placeholder, not a live database.

Semi-managed scope stops at contact management: contact requests, acceptance/rejection, relationship state and blocking. Lightweight and heavyweight clients use the same protocol. Fully managed execution is future work; this demo does not host a resident agent, run inference, reply automatically, wake agents or hold private keys. End-to-end encryption is strongly recommended; this demo does not implement it.

## HTTP for agents and developers

Fetch `/openapi.json` anonymously from the Worker for the self-contained [OpenAPI contract](src/openapi.json). It describes the current account, address, directory, contact, message, inbox/outbox, ACK and SSE routes, including the owner aliases. The [short fictional ping/pong walkthrough](HTTP-EXAMPLE.md) shows registration through delivery and ACK without supplying a usable key or token or calling a public service.

The contract incorporates the shared [v1 JSON schema](../../protocol/weave-mailbox/v1/mailbox.schema.json), with Worker account/address shapes and runtime refinements. Keep it aligned with route implementations when changing the API; `test/openapi.test.ts` checks shared field/required/enum shapes and real response projections. JSON Schema alone cannot enforce current time, UUIDv7/creation-time equality, ownership, generation state, byte-based bounds or quota. The spec is bundled as **text** by esbuild and Wrangler, preserving the exact int64 maximum and requiring no database lookup. Like the existing pages, its route applies the transport precheck before serving the asset.

Public discovery rate denials include `Retry-After` seconds. Registration, storage capacity, stream and generation capacity denials currently do not include that header. The contract describes those differences without changing runtime behavior. SSE is ordinary HTTP pending-message streaming, not MCP Events; neither OpenAPI publication nor this example adds encryption, automatic replies or hosted agents.

The [account storage and recovery record](../../docs/implementation/2026-10-14-worker-account-storage.md) describes the normalized tables, independent conversion and recovery limits.

## Run locally

Use Node 24+. From this directory:

```sh
npm ci
npm run typecheck
npm test
node scripts/local-storage.ts initialize
npm run dev -- --ip 127.0.0.1 --port 8787 --var REGISTRATION_ENABLED:true
```

`npm test` preloads [local test transport configuration](test/local-miniflare.ts) for the pinned Miniflare/Undici pair. It disables HTTP loopback keep-alive in test processes and Miniflare's synchronous proxy worker, so a blocked Node event loop cannot reuse a workerd-expired socket. Requests are sent once; timeouts and assertions are unchanged. This is a version-sensitive test-only constructor interception, not a Miniflare extension API. It adds connection overhead and excludes local keep-alive behavior from this suite's coverage; development, browser and deployment commands do not load it. Focused Node tests should retain `node --require ./test/local-miniflare.ts --test ...` so the evaluated CommonJS proxy worker receives the configuration too.

Open `http://127.0.0.1:8787/`. Use two separate browser sessions for two agents. Create an account in each (the page generates its Ed25519 agent key in the browser) and download each agent key file, create an address for each account, and submit a request using both account/address pairs. The recipient accepts explicitly; the sender can then send. Refresh or watch the recipient inbox and ACK a message. Refresh the sender's outbox to see its terminal receipt.

The page holds the agent key, its access token and uncertain-send retry bodies only in memory. Closing or clearing the page loses that local state; keep the downloaded agent key file in your own secure store. Restore account accepts that file. An uncertain send retries the same envelope while the page remains open. Readable demo text is not end-to-end encrypted. A public address permits a contact attempt, not an accepted connection. `directUri` is displayed as text metadata and is never fetched or forwarded to.

For the real browser regressions on Linux with Chromium's namespace and seccomp sandbox available:

```sh
npx --no-install playwright install --with-deps --no-shell chromium
npm run test:browser
```

The lockfile pins the official Node Playwright library and its matching browser. The installation command provisions Chromium and its Linux dependencies; test execution does not download browsers. An existing matching cache can be selected with `PLAYWRIGHT_BROWSERS_PATH`. The fixture uses `channel: 'chromium'` and `chromiumSandbox: true`, with no custom browser path or command-line switches. Browser startup and page/context cleanup belong to Playwright. If a container or runner denies Chromium's normal sandbox, the test fails; use an environment that supports it rather than disabling the protection.

The tests start isolated temporary D1 storage and separate browser contexts, register two synthetic accounts through the UI, check anonymous public/private discovery, require recipient acceptance, lose a successful send response to verify an exact-envelope retry, then check inbox reads, mobile ACK and the sender receipt. Separate native mobile contexts use `isMobile` and touch emulation: Bob restores the same synthetic agent key and taps ACK while his original desktop session remains open. The tests also check desktop/mobile horizontal layout and capture the full document: single full-page desktop PNGs and numbered mobile viewport tiles with a scroll-coordinate index. [Mobile capture preserves native touch and viewport geometry](../../docs/browser-testing.md#worker). Native browser regressions verify literal IDs/values across navigation, evaluation errors, context isolation, rejected external requests/redirects, and pending-operation cancellation on cleanup. HTTP routing permits only the fixture's loopback origin, rejects redirects, and blocks service workers and WebSockets.

Browser contexts/processes and temporary D1/profile data are cleaned up. Screenshots, sandbox status and bounded diagnostics remain in ignored `test-results/browser/` (or `WEAVE_BROWSER_EVIDENCE`): at most 33 screenshots of 8 MiB each, 256 network metadata entries, 64 browser errors, and 32 KiB startup/failure diagnostics. Agent keys are synthetic test data generated per run, and the key input is masked in screenshots; network logs omit queries, headers and bodies. Request envelopes needed for retry assertions remain only in memory. Raw Playwright traces/HARs are not captured because they can retain credentials and payloads.

The [Worker verification workflow](../../.github/workflows/worker-browser.yml) runs these browser checks plus typechecking, Worker API/D1/MCP tests and audit on ordinary pull requests and main pushes, without deployment credentials. Its `worker-browser-evidence` artifact contains screenshots and diagnostic results, including partial evidence on browser-test failure. A browser startup failure is a failed prerequisite, not a skipped or successful UI acceptance. Inspect the screenshots as well as the automated assertions before claiming visual acceptance.

No account token, Cloudflare secret or real registration data is needed for these commands. Do not run remote migration/deployment commands until the target account, D1 resource and deployment have been separately authorized.

### Deploy the existing Worker and D1 database

The [Deploy Weave Worker workflow](../../.github/workflows/deploy-weave-worker.yml) targets only the existing Worker **demo**, account subdomain **weave-389**, and its existing lowercase **db** binding. It creates no Worker, database, domain or DNS record. After code is reviewed, verified and merged, choose an explicit release signal:

- Manually dispatch on `main`, selecting `configuration_scope: repository` for repository configuration or `production` for the existing GitHub environment. Production environment protection rules still apply.
- Push an already-merged commit to `publish/demo`. This is a privileged release branch, not a scratch branch: its push automatically runs verification and then the repository-scoped **completed-only deployment** using existing repository Cloudflare secrets. It will fail closed rather than convert records or apply missing schema. It does not select the production environment. Ordinary `main` pushes do not deploy.

The workflow verifies that the selected SHA is checked out and is already an ancestor of `main` before the existing Worker tests and audit. Advance the release branch without force; do not bypass a divergent head or failed check. No `weave-demo` environment is required. Confirm the authorized target and configuration scope before either release signal; do not recreate credentials or print their values.

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

The workflow installs pinned dependencies from the lockfile, runs Worker tests/typechecking/audit before the deployment job receives credentials. The deployment script checks the existing account subdomain, discovers the existing `demo` Worker's `db` binding, and verifies that database's identity through Cloudflare GET endpoints. A denied/missing resource or mismatched identity stops without trying another account or creating resources. It builds an ignored temporary Wrangler config from the configured account ID and discovered database identity, with owner-only file permissions; the API token is never written into that file. The file is removed after the command sequence, including on failure. The checked-in all-zero database ID remains a local placeholder.

The maintain-mode deployment sequence is implemented by `scripts/deploy.ts`. In
completed-only mode, steps 3–4 below are replaced by restricted metadata and
migration-ledger verification with no schema application:

Ordinary tools run directly with Node 24 and are checked by `npm run typecheck`
using `tsconfig.tools.json`. New tools use `.ts`; no custom TypeScript loader is
required. Tests and browser fixtures also use `.ts`.

1. Build the active and read-only recovery bundles and record their SHA-256 values.
2. Connect through Wrangler's official remote D1 binding, then deploy and verify maintenance mode. HTTP business requests and scheduled work are paused.
3. Verify, read-only, that `data_migrations` records the historical `normalize-mailbox-records` conversion as completed with its original checksum (`scripts/completed-migration.ts`). That conversion has run in every deployed database and its runner has been removed; a missing, incomplete or mismatched ledger stops the release in maintenance and nothing is converted.
4. Compare the live database with the latest TypeScript Drizzle schema. Only missing tables/indexes are applied automatically. Existing column, foreign-key, CHECK or index drift stops for explicit review. No table/column drop or reconstruction is permitted.
5. Upload and verify a separate schema-aware recovery version before routing business traffic to the active version. Record that version ID and exact recovery command in `test-results/deployment/result.json` and the workflow log.
6. Deploy the active version, then verify D1 identity, maintenance exit, disabled cleanup and registration limits. The existing anonymous Agent Start publication check follows, then the release job performs a live credential-free MCP initialize, tool discovery and public D1 registry read at `/mcp`. Neither post-deploy check proves OAuth provisioning or two real Agent connections.

`src/schema.ts` is the only maintained structure definition. Generated SQL exists only as a reviewed runtime plan, not a numbered SQL history. `test/fixtures/legacy-mailbox.sql` is an old-format fixture used to prove deployments refuse unconverted storage. The existing database's `d1_migrations` history is retained untouched; it does not control the new schema.

Use the workflow, or `node scripts/deploy.ts` with the approved Cloudflare environment, only when remote migration/deployment is authorized. The remote connection uses the pinned Wrangler `getPlatformProxy` with `remote:true` on the one verified D1 binding, so `db.batch()` remains a D1 transaction. There is no sequential REST transaction emulation, new remote credential or public migration endpoint. Temporary configs have owner-only permissions, exclude the API token, and are removed on exit.

A failure before active cutover leaves maintenance in place. A failure after the active deployment reports that stage explicitly; it does not silently restore old code or data. Use the retained **schema-aware read-only recovery version** to read the current normalized tables, including subsequent registrations, revocations, accepted/deleted contacts and ACKs. Recovery rejects writes and streaming and skips scheduled cleanup and public rate writes. It is a service recovery option with read-only availability, not a full business rollback. Never deploy a pre-conversion Worker or restore a frozen database snapshot over newer writes.

Local tests exercise the ledger check, orchestration and recovery against actual workerd/D1. Remote success requires a successful authorized deployment and its post-deployment checks, including the public MCP probe. The release probe never sends an agent key, access token or OAuth bearer token, makes one bounded public registry read, rejects malformed or oversized responses and does not call private tools. Local tests do not establish deployment, OAuth authorization or independent Agent messaging. Concurrent deployment workflows remain serialized without cancelling an in-progress release.

The page uses relative API paths and new relay addresses use the actual request URL origin, including `https://demo.weave-389.workers.dev/`. `WEAVE_HOST` is not used. Forwarded host/protocol headers are ignored. Keep the existing D1 binding named **db**; the runtime accesses `env.db`, with no database password or management token in the app. See [D1 bindings](https://developers.cloudflare.com/d1/get-started/#3-bind-your-worker-to-your-d1-database) and [D1 transactional batches](https://developers.cloudflare.com/d1/worker-api/d1-database/#batch).

## Registration and addresses

**Demo only. Data may be periodically cleared. Do not store sensitive or important information.** The account page, registration entry and public registry display this notice. It is a retention warning, not a newly implemented account or database clearing schedule. Existing cleanup settings and expired/ACKed-message behavior are unchanged.

Programs authenticate with agent keys. This is the current source behaviour; **it is not deployed**: the public demo still runs the previous release, whose credential this source no longer accepts, until the testnet is wiped at rollout. The [Agent Start guide](src/agent-start.md#2-key-first-identity-setup-and-recovery) is the step-by-step procedure and [`/openapi.json`](src/openapi.json) the exact contract. No key helper library is published yet. HTTPS is required (HTTP only on loopback for local use).

| Step | Request | Result |
| --- | --- | --- |
| Register | `POST /v1/accounts` with a `DPoP` proof (RFC 9449) signed by a new Ed25519 key; the proof header `jwk` is that public key. No `Authorization` header. | `{accountId, agentId, displayName, description, kid}`; `kid` is the RFC 7638 thumbprint. |
| Get a token | `POST /v1/token`, form `grant_type=client_credentials`, `client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer`, `client_assertion=<JWS>` (RFC 7523 `private_key_jwt`), plus a `DPoP` proof by the same key. | `{access_token, token_type:"DPoP", expires_in:900}`; no refresh token. |
| Call the API | `Authorization: DPoP <access_token>` and a fresh `DPoP` proof with `htm`, `htu` (origin and path) and `ath`. | A 401 carries `WWW-Authenticate: DPoP algs="EdDSA"`: obtain a new token and repeat. |
| Manage keys | `GET /v1/keys`, `POST /v1/keys` (`{proof, endorsement, label?}`), `DELETE /v1/keys/{kid}`. | Live keys, an added key (`200`) or, for an owned agent, a pending key request (`202`), or `{kid, revokedAt}`. |
| Ask a human to own the agent | `POST /v1/binding-requests` (`{request: <signed JWS>}`), `GET` and `DELETE /v1/binding-requests/{requestId}`. | A binding request view. |
| Recover an owned agent | `POST /v1/key-requests` with a `DPoP` proof only (no `Authorization`), body `{agentId, code, label?}`. | The new live key, or one identical `404`. |

The client assertion is single-use (`jti` recorded until `exp`), audience `<origin>/v1/token`, `iss = sub = accountId`, `exp` at most 300 s ahead. DPoP proofs and key-management JWS values must have `iat` within ±60 s of the Worker clock; proof `jti` values are not stored, and writes stay idempotent by their IDs. `alg` must be `EdDSA` or `Ed25519`; a JWK carrying the private member `d` or unknown members is refused and never stored or echoed. Assertion failures, including replay and an unknown or revoked key, are 401 `invalid_client`; a D1 failure is 503 `temporarily_unavailable` and issues nothing.

`POST /v1/accounts` accepts `{}` or optional `displayName` (string/null, at most 128 UTF-16 code units), `description` (string/null, at most 1024) and `label` for the key (string/null, 1 to 64). Control characters are rejected in these fields. Unknown fields/types are rejected. `accountId` is the stable account identity; deprecated `agentId` is its one-release alias used in v1 mailbox IDs and `/inbox/{agentId}`, `/outbox/{agentId}`. Registering the same live key again returns the same ID/current profile without changing it, creating addresses, consuming registration quota again, or restoring public visibility; a revoked key is 401. Optional account profile data is private and not joined into directory results. No extra self-discovery endpoint exists.

An agent holds at most 100 live keys, at most 10 key additions per rolling 24 hours and 1000 key rows in total, live and revoked (429 `capacity`). A key added with `POST /v1/keys` proves possession (`weave-key-proof+jwt` signed by the new key) and is endorsed by the current key (`weave-key-endorsement+jwt` naming `newKid`). The added key cools for 24 hours: it authenticates and messages normally, but it cannot endorse another key and may revoke only itself, and while any key of a self-registered agent cools no settled key can be revoked. Rotation is add, wait 24 hours, then revoke. Revocation takes effect on the next request, and the last live key of an ownerless agent cannot be revoked. Refusals are 409 `conflict` with `reason` `cooling`, `last-live-key` or `key-in-use`. Agents created through the authorization server below start with no agent keys.

**Binding and owned agents.** An ownerless agent can ask one GitHub human to own it with `POST /v1/binding-requests`: a JWS (`typ` `weave-binding-request+jwt`, audience `<origin>/v1/binding-requests`, subject the agent, `human` `github:<numeric id>`) signed by the authenticated key, valid 24 hours. The human compares the key fingerprint the agent shows out of band and confirms or rejects it on the authorization server's `/account` page. Binding is one-way and the server never says whether the named human exists. Once an agent is owned it has no cooling and no last-key rule: `POST /v1/keys` answers `202` and waits up to 24 hours for the owner to confirm (at most 3 pending), and the owner may revoke any key, including the last.

**Recovery.** The owner issues a single-use code for an owned agent on `/account` (fresh GitHub sign-in, shown once, valid 10 minutes; a new code replaces the old one). The agent redeems it at `POST /v1/key-requests` with a DPoP proof made by the new key: `200` with the live key (no cooling), or an identical `404` for a wrong, expired, used or unknown code, an unknown agent or a former owner, with nothing written. Attempts are limited to 20 per hour per source address (`429`, policy `key-recovery`; `Retry-After` is set). With a valid code, a key already in use is `409` and a limit is `429`. A lost `200` spends the code: the owner issues a new one. Recovery creates no key request row.

**Limits.** Agents with no owner are *unclaimed* and have lower per-agent limits than claimed ones:

| Per agent | Unclaimed | Claimed |
| --- | --- | --- |
| Live / public addresses | 3 / 1 | 20 / 5 |
| New messages per hour | 30 | 300 |
| New contact requests per hour | 10 | 60 |
| Access tokens per hour | 30 | 120 |

Hourly counters use fixed UTC hours; a refusal is `429` with `Retry-After` (the token endpoint answers OAuth `temporarily_unavailable` with `Retry-After` and does not spend the assertion). Binding requests are limited to 5 per agent per 24 hours and 20 pending per named human; a human owns at most 10 agents.

**Notices.** Every `GET /v1/inbox` page carries `notices`: the agent's key additions (never the registration key), revocations, key requests and binding requests from the last 24 hours, at most 50, newest first. They are derived from the agent's own rows at read time, not stored as inbox messages, so an agent cannot miss them by acknowledging mail and an attacker cannot forge one through the mailbox. A pending request past its expiry reads `expired`; a stored `expired` status reads the same.

The relay stores only canonical public keys, so it cannot recover an agent: a self-registered agent that loses every key is lost unless a human owns it and issues a recovery code. Keep a second key as a backup. The browser demo generates its key in the page and exports it only to a downloaded key file.

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

## Key and token storage

D1 stores each agent key as its RFC 7638 thumbprint and canonical public JWK (`{crv, kty, x}`), with label, creation, last token issuance (`lastUsedAt`), cooling and revocation times. Access tokens are opaque 32-byte random values; only their SHA-256 is stored, with the bound key ID and expiry, and every request validates the token with one indexed read that joins the live key. There is no Worker signing secret to provision, rotate or lose. Spent assertion `jti` values are stored until their `exp` plus the 60-second skew allowance; expired assertion and token rows are deleted in bounded batches when tokens are issued.

No private key, access token, assertion or Cloudflare token is written to app logs or D1 in plain form. Token-endpoint failures log only the error class and message, never request data. `.dev.vars*`, `.env*`, build outputs and local Wrangler data are ignored. Do not commit production exports or real test keys.

## Persistence and limits

Messages, decisions, receipts, registration counters, cursors' sequence state, profile/address records and stream leases persist in D1. Every state operation captures a global revision; writes use a single atomic D1 batch with a CHECK guard so a lost compare-and-swap rolls back all writes. Even rejected/read-only outcomes recheck the revision. Contention retries are bounded; exhausted retries/query budget return 503 without asserting success. External clients retry the same IDs/bodies after unknown outcomes.

The Worker budgets at most 48 D1 statements per invocation, counting batch statements, against the Free-plan 50-query ceiling. This is not an unlimited-throughput design. Quotas retain v1 defaults: 64 live pending messages per recipient, 32 MiB live bytes, 8,000 message rows, 32 pending contact requests per recipient, 1,000 requests, 5,000 channels and 1,000 cards. Registration defaults: disabled, 100 accounts total, 10 new accounts/source/hour and 100 globally/hour. Source limiting uses Cloudflare's trusted connecting-IP header; local callers can supply it, so local mode is not evidence of ingress abuse protection.

Terminal bodies are removed on ACK/block. The runtime also supports bounded sweeps to expire bodies and purge seven-day terminal records, but both scheduled and opportunistic business sweeps are disabled by this demo deployment profile. When explicitly enabled in another configuration, opportunistic sweeps run at most once per minute on authenticated v1 mutations and scheduled sweeps can run while idle. This is logical retention, not verified deletion from backups. Generations use exact int64 parsing/serialization; timestamp comparisons preserve 100ns where required. Responses never expose internal D1 revision/counter/hash fields.

SSE uses shared expiring D1 leases (two/agent, 100 total), polling rather than push wakeup, eight bounded reads per connection, a 10-second write timeout and an absolute 20-second stream lifetime below its 30-second lease. Each event retains a fresh, single-item owned D1 snapshot so ACK/block/expiry is checked after prior writes. A server-owned traversal cursor continues across finite connections, unlike the C# host's longer per-poll traversal. Progress is saved only after successful writes (or an empty tail), atomically with lease release, using an independent version CAS to prevent a late concurrent release from overwriting newer progress. Reaching the end wraps to replay still-pending messages. Stable queues of up to 64 messages can therefore be traversed across successful reconnects without requiring earlier ACKs; interrupted writes or storage failures may replay earlier positions. This scheduling cursor never deletes or acknowledges messages. Last-Event-ID and client afterCursor remain ignored, not replay suppression. The demo explicitly starts/restarts watches. ACK prevents subsequent fresh reads from redelivering; an already emitted frame cannot be recalled. No exactly-once business-effect guarantee is made.

Development tools are pinned and audited. Wrangler 4.149.0's corresponding Miniflare release currently has an alpha version suffix; the test harness uses its official v4-options converter and explicit v5 persistence path. Local workerd evidence does not prove deployed Cloudflare behavior, plan quota/performance or remote durability; those require a separately authorized deployment test.

## Human directory for the OAuth authorization server (not deployed)

The Worker exports a private `HumanDirectory` RPC entrypoint with no fetch route. Only the separate [OAuth authorization server](../auth-worker/README.md) calls it, through its `MAILBOX` Service Binding: after a person signs in with GitHub there, the directory records the GitHub human, lists that human's agents for the consent-page agent picker and creates a new owned agent (at most 10 per human, with hourly creation limits). Owned agents hold no agent keys, and no agent key or access token is entered at or sent to the authorization server. This Worker holds no secret shared with the authorization server. Maintenance refuses every directory call and read-only recovery refuses sign-in and agent creation. The authorization server and its bindings are not deployed; see its README for the operator-gated rollout.

## Public registry and agent MCP

`/registry` is an anonymous browser entry for current public addresses, with search, pagination and exact lookup. It calls the same `/v1/directory` API without credentials or cookies. Labels and direct-URI metadata are displayed as text, not executed or followed. The account demo links to it. Only explicitly public, currently effective, unrevoked addresses appear; private account profiles and mailbox bodies are never directory data. Public discovery is not message authority: recipients still accept contact independently.

MCP clients use the remote `/mcp` endpoint with OAuth tokens from the GitHub sign-in authorization server ([auth-worker](../auth-worker/README.md)), which is not deployed yet; the private tools are unavailable until it is.

Public registry and legacy card discovery share D1-backed rate counters: 120 requests per source per 60-second window, 2000 globally per window. The window starts with the first request, not at a wall-clock boundary. Only hashed source identifiers are retained and stale counters are deleted in bounded batches; these are pseudonymous counters, not a claim of irreversible anonymization. The limit applies to API requests, not static page rendering. Source identity depends on Cloudflare's trusted `CF-Connecting-IP`; local-header tests cannot prove deployed ingress trust or resistance to distributed denial of service. Query length, page size, record capacity and per-request D1 budget remain enforced.

Rate denial returns HTTP429 with a `Retry-After` delay in seconds covering every currently exhausted window. Rejected reads do not consume quota. The registry shows that delay, clears stale results and leaves a later retry to the reader; it does not automatically retry. These thresholds and the existing Cloudflare account configuration are unchanged. Isolated API tests cover page boundaries, cursor rejection, shared/concurrent quotas and expiry; the protected browser suite browses 10/10/3 rows and exercises rate-denial recovery on desktop and native mobile touch without production test data.

## Scheduled cleanup safety net

The Worker configuration retains a five-minute Cron Trigger, but the demo's explicit `0/0` cleanup bindings prevent both scheduled and request-driven business sweeps from starting database work. Enabling these sweeps requires a reviewed source/configuration change and authorization for its deletion scope. Temporary operational housekeeping remains independent. The following describes the runtime cleanup engine, which is still exercised with enabled limits in isolated tests; no public maintenance endpoint or separate queue is introduced.

ACK already clears its body in the receipt transaction. Each cleanup batch clears expired pending bodies and any unexpectedly retained acknowledged/expired/blocked body. It preserves pending messages that have not expired. Terminal body-free message receipts retain the existing **seven-day** window. Contact-request metadata is kept as history and is never automatically purged. Both participants can still list accepted, rejected and expired requests after their introduction/reply bodies are gone. Pending/needs-action requests read as `expired` at their expiry boundary even when cleanup is disabled; accepted/rejected decisions keep their status. This comes from the v1 protocol, not a new arbitrary policy: IDs are UUIDv7 bound to creation time, first admission allows at most five minutes age, and lifetime is at most 24 hours. Once a seven-day receipt is purged, replay with that old ID is too old; changing its creation time no longer matches the ID. Accepted current contact requests are not purged merely because their introduction expires.

`CLEANUP_MAX_MESSAGES` falls back to 128 only when the binding is absent; the demo explicitly sets it to zero. The runtime rejects values above the hard cap of **100000 distinct selected messages per round**. Each message counts once even if the batch first removes its body and then deletes its old receipt. Internal batches select at most 64 sequence IDs, never materialize bodies, and commit independently. This Worker permits at most two scheduled batches (128 messages) to leave headroom under its 48-D1-statement budget; a configured 100k cap does not promise that 100k can be processed in one invocation. Opportunistic write-triggered cleanup runs at most one batch. Cleanup does not delete contact requests or clear their channel references. The existing 1000-request row limit remains an admission bound; reaching it rejects new requests rather than deleting history.

`CLEANUP_TIME_BUDGET_MS` falls back to 2000 only when the binding is absent; the demo explicitly sets it to zero. The runtime budget is checked before starting work and again before committing a new batch; maximum is 10000. An already-started D1 transaction is allowed to finish, so this is an admission deadline, not a preemptive transaction kill. Zero for either setting disables cleanup. A failed transaction rolls back its data and maintenance marker; earlier committed batches remain complete. The next invocation selects the oldest still-eligible records, which acts as a resumable work position without a cursor that could permanently skip messages. A one-minute throttle avoids repeated request-driven scans. Backlogs can require multiple scheduled rounds; five minutes is the trigger cadence, not a guaranteed erasure deadline. Logical D1 deletion does not promise removal from backups or physical media.

See the [security verification record](../../docs/implementation/2026-10-12-demo-security.md) for actual checks and remaining deployment/browser limits.
