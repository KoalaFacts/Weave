# Weave per-user OAuth authorization server (NOT deployed)

**Operator-gated integration source, not real live OAuth acceptance.** This
separate Cloudflare Worker uses the official `@cloudflare/workers-oauth-provider`
v1.2.4 split-server API. It cannot be used until explicitly authorized KV,
SQLite Durable Objects, a GitHub OAuth app, secrets, HTTPS issuer and both
Service Bindings exist.

Flow: public OAuth client discovers the trusted issuer; SDK validates exact
client/redirect, RFC8707 resource and PKCE S256. A browser without a valid
human session is sent to **GitHub sign-in** (state + PKCE S256, callback
`/login/github/callback`); the AS resolves the GitHub user to a Weave human
through the mailbox Worker's private `HumanDirectory` RPC (`MAILBOX` Service
Binding) and sets a short-lived signed human-session cookie. Frozen humans are
refused. The SDK's browser-bound `beginConsent` handle is linked to an
independently random 256-bit nonce kept in a per-handle SQLite Durable Object.
The consent page identifies the client, redirect domain, requested scopes and
the signed-in GitHub login, and offers an **agent picker**: one of the human's
own agents, or a new agent (at most 10 per human, with hourly creation limits).
The user explicitly chooses Allow or Deny. On Allow the AS validates the SDK
consent cookie, re-reads the human session, atomically consumes the nonce in a
DO transaction, then creates the new agent or confirms the chosen one is owned
by that human. Client form fields cannot change scopes, subject or target, and
no agent key or agent access token is ever entered at, sent to or held by the AS.

**Owner page.** `/account` lists the signed-in human's agents, pending binding requests (with the requesting key's fingerprint) and pending key requests. The human can confirm or reject binding and key requests, revoke keys (the last key too: the owner is the recovery path) and issue a single-use recovery code for an agent. Confirming, revoking and issuing a code need a GitHub sign-in from the last 10 minutes, otherwise the page sends the human through GitHub again; rejecting does not. Every form carries a signed action token that lives 10 minutes; an older one returns `?result=form-expired` and asks the human to reload. The binding confirmation also signs a digest of the agent's live keys as shown, so a key added or revoked after the page loaded returns `?result=keys-changed` and nothing is bound. The recovery code appears only in the response body, once. GitHub offers no `max_age`, `prompt=login` or `auth_time`, so this is an interactive account-selection step, not proof that credentials were re-entered: whoever controls the GitHub account can manage that human's agents. The page changes mailbox D1 only through the private `HumanDirectory` binding. See the [agent binding record](../../docs/implementation/2026-11-01-agent-binding.md).

Upon approval the SDK issues the code and handles PKCE exchange, refresh and
revocation using its KV token store. Access tokens last **900 seconds**, with
`userId` and `props.accountId` both equal to the chosen agent's D1 account.
The Weave resource's private `AUTH_SERVER.validateToken(resource, token)`
RPC remains mandatory for **each** private operation, including D1 existence,
scope, audience and expiry checks. No shared account, anonymous private tool,
automatic consent or external control forwarding is present.

The library's consent KV handle alone uses non-atomic read/delete. DO storage
transactions prevent a parallel replay of the same consent from issuing a
second grant. State storage, data retention and cost require review/approval.
The `wrangler.example.jsonc` template intentionally contains placeholder
bindings and `workers_dev:false`; **do not deploy it or apply migration v1**.

Before deployment: approve separate Cloudflare AS domain, KV and DO, register a
GitHub OAuth app whose callback is `<issuer>/login/github/callback`, set its
public `GITHUB_CLIENT_ID` var, install the `GITHUB_CLIENT_SECRET` and an
independently generated 32-byte base64url `HUMAN_SESSION_KEY` as AS Worker
secrets (never commit/print), bind `MAILBOX` to the mailbox Worker's
`HumanDirectory` entrypoint, configure
the operator-owned MCP Worker's `AUTH_SERVER` Service Binding to
`service:weave-auth-demo` (default `AuthServer` WorkerEntrypoint; no named `entrypoint`), and review customer consent,
CIMD/DCR policy and revocation. Client ID Metadata Documents (strictly public
fetch) are the primary client path; open dynamic client registration at
`/register` is a fallback behind two rate-limit bindings: `REGISTRATION_LIMITER`
allows 10 per minute per source address (an IPv4 `CF-Connecting-IP` as is, an
IPv6 one by its /64, an IPv4-mapped IPv6 one as its IPv4 address, anything
else as one shared `unknown` key), and `REGISTRATION_GLOBAL_LIMITER` allows 100
per minute across all addresses. Either budget refused returns 429 with
`Retry-After: 60`. A missing binding or a limiter fault returns 503 and the
registration endpoint is advertised in metadata only when both are bound.
Both are Cloudflare rate-limit bindings: per-location, eventually consistent
budgets, not an exact global cap. Under abuse the global budget can deny the
DCR fallback to everyone for a minute; CIMD clients are unaffected because they
never call `/register`. Validate source SHAs, Node24 `npm ci`/typecheck, real workerd KV/DO +
MCP D1 E2E, full repository CI/coverage/security/licence and independent
review before any merge/release. The committed dependency locks and branch workflow cover Node24 source/type checks,
portable logic, real workerd Durable Object replay, the bundled production mailbox
Worker and actual D1 schema, simulated GitHub sign-in through the real
`HumanDirectory` binding, agent picking and creation, token issuance for the
chosen agent and, with tokens from that simulated sign-in, six alternating
private MCP messages with ACKs between two humans' agents, and one
unacknowledged message in each direction recovered across a cold Worker
restart. These test-owned synthetic
humans and agents do NOT establish a public or two-independent-model-Agent acceptance.
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
namespace in `WEAVE_AUTH_KV_NAMESPACE_ID`, the approved GitHub OAuth app's
public client ID in `GITHUB_CLIENT_ID`, plus
`OAUTH_ISSUER=https://weave-auth-demo.weave-389.workers.dev` and
`OAUTH_RESOURCE=https://demo.weave-389.workers.dev/mcp`.
Then run `npm run prepare:demo`. It writes
`wrangler.demo.generated.jsonc` exclusively (not in git) with mode 0600,
only public issuer/resource/client-ID values, resource IDs and the `MAILBOX`
Service Binding to the `demo` Worker's `HumanDirectory` entrypoint. No token,
GitHub client secret or session key is written, and the generated manifest has
no `REGISTRATION_LIMITER` or `REGISTRATION_GLOBAL_LIMITER`, so open dynamic client registration stays closed and unadvertised. The test workflow independently bundles this generated manifest
with Wrangler `--dry-run` using **dummy** IDs; neither CI nor this script
deploys the Worker or creates KV/DO state.

**Stop at this point until deployment is separately authorized.** Approval
must also cover the OAuth Service Binding on the actual resource Worker,
the AS-only `GITHUB_CLIENT_SECRET` and `HUMAN_SESSION_KEY` secrets (installed
separately on the AS Worker, never in a manifest; the resource Worker needs no
shared secret), Durable Object migration, revocation/retention,
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
approved KV and SQLite Durable Object, the `GITHUB_CLIENT_ID` var, the
`GITHUB_CLIENT_SECRET` and `HUMAN_SESSION_KEY` secrets and the `MAILBOX`
Service Binding to the `demo` Worker's `HumanDirectory` entrypoint. Never pass
a secret as a command argument or commit it. Review the GitHub sign-in, consent,
agent creation, token revocation and retention policy.

Do **not** install `OAUTH_ISSUER` or `OAUTH_RESOURCE` separately on the live
resource Worker: that would enable incomplete OAuth configuration and fail
closed before the private service exists. Once an authorized operator has
independently verified the AS, the normal protected demo deploy procedure
can be invoked with the **one-time** environment flag
`WEAVE_APPROVED_FIRST_PARTY_OAUTH_BINDING=true`. The release script first
performs read-only Cloudflare metadata checks of the already deployed AS,
its existing KV/DO bindings, GitHub client ID, both sign-in secrets and the
exact `MAILBOX` binding to `demo`/`HumanDirectory`, and anonymous RFC 8414 issuer/CIMD/PKCE
discovery. Any missing, mixed or mismatched prerequisite aborts **before**
maintenance, schema processing or an upload. It never provisions KV, DO,
credentials, D1 human or agent records or the AS Worker itself.

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
AS metadata and browser PKCE login, GitHub sign-in and explicit consent for
**two independent humans**, private tools, contact, six messages with ACK and cold
restart/replay.

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
D1. The live AS, KV, SQLite DO, GitHub OAuth app, sign-in secrets, both Service Bindings and
preflight/rollback must be separately authorized, provisioned and reviewed.
No cloud change occurs by adding this release selector.
