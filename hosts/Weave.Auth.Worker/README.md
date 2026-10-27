# Weave per-user OAuth authorization server (NOT deployed)

**Operator-gated integration source, not real live OAuth acceptance.** This
separate Cloudflare Worker uses the official `@cloudflare/workers-oauth-provider`
v1.2.4 split-server API. It cannot be used until explicitly authorized KV,
SQLite Durable Objects, secrets, HTTPS issuer and MCP Service Binding exist.

Flow: public OAuth client discovers the trusted issuer; SDK validates exact
client/redirect, RFC8707 resource and PKCE S256. The SDK's browser-bound
`beginConsent` handle is linked to an independently random 256-bit nonce kept
in a per-handle SQLite Durable Object. The page identifies the client,
redirect domain, requested scopes, and link to Weave's first-party
`/account-proof?challenge=...`. Only **Weave**, never the AS, receives the
existing long-lived account control. User pastes a signed 60-second `wop1`
ownership proof at the AS and explicitly chooses Allow or Deny. The AS verifies
HMAC signature, exact account UUID/issuer/resource/nonce and expiry, then
atomically consumes the nonce in a DO transaction before consuming the SDK
cookie-bound grant. Client form fields cannot change scopes, subject or target.

Upon approval the SDK issues the code and handles PKCE exchange, refresh and
revocation using its KV token store. Access tokens last **900 seconds**, with
`userId` and `props.accountId` both equal to the proven existing D1 owner.
The Weave resource's private `AUTH_SERVER.validateToken(resource, token)`
RPC remains mandatory for **each** private operation, including D1 existence,
scope, audience and expiry checks. No shared account, anonymous private tool,
automatic consent or external control forwarding is present.

The library's consent KV handle alone uses non-atomic read/delete. DO storage
transactions prevent a parallel replay of the same signed proof from issuing a
second grant. State storage, data retention and cost require review/approval.
The `wrangler.example.jsonc` template intentionally contains placeholder
bindings and `workers_dev:false`; **do not deploy it or apply migration v1**.

Before deployment: approve separate Cloudflare AS domain, KV and DO, provision
both matching 32-byte signing-key secrets (never commit/print), configure
the operator-owned MCP Worker's `AUTH_SERVER` Service Binding to
`service:weave-auth-demo, entrypoint:AuthServer`, and review customer consent,
CIMD/DCR policy and revocation. Dynamic client registration is disabled by
default; Client ID Metadata Documents are available through strictly public
fetch. Validate source SHAs, Node24 `npm ci`/typecheck, real workerd KV/DO +
MCP D1 E2E, full repository CI/coverage/security/licence and independent
review before any merge/release. The committed dependency locks and branch workflow cover Node24 source/type checks,
portable logic, real workerd Durable Object replay, the bundled production mailbox
Worker and actual D1 schema, individually registered test accounts and Weave-issued
account proofs, six alternating private MCP messages with ACKs, and two unacknowledged
opposing messages recovered across a cold Worker restart. These test-owned synthetic
accounts do NOT establish a public or two-independent-model-Agent acceptance.
This remains **isolated workerd emulation, not public Cloudflare deployment,
real human-account consent or independent live model-Agent acceptance**, and
does not replace full repository or production gates. It is not ready for deployment.

Next live acceptance remains #236: two distinct real accounts/agents, separate
consents, private contact request/accept, six alternating messages and ACKs,
cold restart and duplicate-replay persistence, with sanitized evidence.

## Operator-reviewed demo OAuth launch preparation (no live deployment)

After **separate explicit approval** of the Cloudflare OAuth/KV/SQLite Durable
Object resources, retention and consent flow, the operator may use an existing
account's Workers.dev origin instead of provisioning a custom domain. This
nonpublishing preparation path fixes the exact auth origin to
`https://weave-auth-demo.weave-389.workers.dev` and the resource to
`https://demo.weave-389.workers.dev/mcp`; it refuses arbitrary origins.

From `hosts/Weave.Auth.Worker`, with Node 24 and locked dependencies installed,
set `CLOUDFLARE_ACCOUNT_ID` and **the ID of an already approved/existing**
namespace in `WEAVE_AUTH_KV_NAMESPACE_ID`, plus
`OAUTH_ISSUER=https://weave-auth-demo.weave-389.workers.dev` and
`OAUTH_RESOURCE=https://demo.weave-389.workers.dev/mcp`.
Then run `npm run prepare:demo`. It writes
`wrangler.demo.generated.jsonc` exclusively (not in git) with mode 0600,
only public issuer/resource values and resource IDs. No token or proof key is
written. The test workflow independently bundles this generated manifest
with Wrangler `--dry-run` using **dummy** IDs; neither CI nor this script
deploys the Worker or creates KV/DO state.

**Stop at this point until deployment is separately authorized.** Approval
must also cover the OAuth Service Binding on the actual resource Worker,
the common `OAUTH_ACCOUNT_PROOF_KEY` secret (installed separately on both
Workers, never in a manifest), Durable Object migration, revocation/retention,
and either the reviewed external-subject D1 link migration or the verified
first-party Service Binding owner model. The normal `demo` deployment
intentionally cannot create these resources or silently enable them. An
approved operator deployment must record the exact source SHA, Worker version,
the same-origin metadata and browser PKCE consent proof, and its rollback.
Even a green Wrangler dry run is **not** remote OAuth access or #236's
two-independent-agent public proof.
