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
review before any merge/release. The committed dependency lock and branch workflow cover Node24 source/type checks,
portable logic and real workerd Durable Object consent replay and cold restart.
This remains **local emulation, not real cloud authorization, live AS consent or
private MCP traffic**, and does not replace full repository or production gates. It is not ready for deployment.

Next live acceptance remains #236: two distinct real accounts/agents, separate
consents, private contact request/accept, six alternating messages and ACKs,
cold restart and duplicate-replay persistence, with sanitized evidence.
