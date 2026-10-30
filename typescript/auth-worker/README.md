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
`service:weave-auth-demo` (default `AuthServer` WorkerEntrypoint; no named `entrypoint`), and review customer consent,
CIMD/DCR policy and revocation. Client ID Metadata Documents (strictly public
fetch) are the primary client path; open dynamic client registration at
`/register` is a fallback limited to 10 per minute per `CF-Connecting-IP` by the
`REGISTRATION_LIMITER` rate-limit binding (absent binding: `/register` returns 503). Validate source SHAs, Node24 `npm ci`/typecheck, real workerd KV/DO +
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

From `typescript/auth-worker`, with Node 24 and locked dependencies installed,
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

## One-time, approved first-party AS-to-demo binding rollout (not executed)

The existing demo release keeps OAuth disabled by default. Only **after separate
operator approval** of live authorization-service and resource-worker changes,
provision the existing `weave-auth-demo` Worker at the fixed issuer above, with
approved KV and SQLite Durable Object, and install the same independently
generated 32-byte `OAUTH_ACCOUNT_PROOF_KEY` secret in that Worker **and**
in the existing `demo` resource Worker. Never pass the secret as a command
argument or commit it. Review the consent, token revocation and retention policy.

Do **not** install `OAUTH_ISSUER` or `OAUTH_RESOURCE` separately on the live
resource Worker: that would enable incomplete OAuth configuration and fail
closed before the private service exists. Once an authorized operator has
independently verified the AS, the normal protected demo deploy procedure
can be invoked with the **one-time** environment flag
`WEAVE_APPROVED_FIRST_PARTY_OAUTH_BINDING=true`. The release script first
performs read-only Cloudflare metadata checks of the already deployed AS,
its existing KV/DO/secret bindings and anonymous RFC 8414 issuer/CIMD/PKCE
discovery. Any missing, mixed or mismatched prerequisite aborts **before**
maintenance, schema processing or an upload. It never provisions KV, DO,
credentials, D1 identity links or the AS Worker itself.

After that preflight, the same versioned maintenance/recovery/active release
stages the fixed `AUTH_SERVER` entrypoint service binding and both fixed
issuer/resource values together. Each stage verifies the effective binding;
missing service/metadata stops before subsequent stages. Remove the one-time
flag for later ordinary releases; the script will preserve only already
installed operator-approved bindings without creating a new one.

This path itself **performs a live Worker release when invoked**; it is not
approval to run it, nor a successful authorization proof. The operator must
first approve this exact source SHA and rollout/rollback with the existing
protected release gate. After an authorized release, separately verify actual
AS metadata and browser PKCE login/explicit consent for **two independent
existing users**, private tools, contact, six messages with ACK and cold
restart/replay. Keep the `oauth_identity_links` D1 schema opt-in disabled
unless its separate privacy/migration/rollback is approved.

### Protected GitHub Actions one-time OAuth release selector (not executed)

The **Deploy Weave Worker** workflow exposes `enable_first_party_oauth`
(default **false**) only through manual dispatch on protected `main`,
with `configuration_scope=production`, `database_mode=completed-only`
and a separately approved `production` environment. The Node24
release-intent gate validates these constraints before other release work
and again after the environment approval. Only the protected production
job receives `WEAVE_APPROVED_FIRST_PARTY_OAUTH_BINDING=true`; normal
`publish/demo` pushes and repository releases cannot activate OAuth.
This checkbox is NOT approval to create resources, install secrets or migrate
D1. The live AS, KV, SQLite DO, proof signing secret, Service Binding and
preflight/rollback must be separately authorized, provisioned and reviewed.
No cloud change occurs by adding this release selector.
