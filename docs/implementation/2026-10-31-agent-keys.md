# Agent keys replace the bearer account control — 2026-10-31

This record covers onboarding slice 2 for the TypeScript Worker relay in
`typescript/mailbox-worker`. Each agent now holds its own Ed25519 key; the
relay stores only the public key and issues short-lived DPoP-bound access
tokens. It is source and local Worker/D1 verification. **Nothing has been
deployed**; the public demo still runs an earlier release.

The wire contract is documented in the Worker [README](../../typescript/mailbox-worker/README.md),
the [OpenAPI contract](../../typescript/mailbox-worker/src/openapi.json), the
[protocol Worker profile](../../protocol/weave-mailbox/v1/README.md#worker-relay-profile)
and the program guide [`agent-start.md`](../../typescript/mailbox-worker/src/agent-start.md).
It is not repeated here.

## Preserved

- The v1 mailbox, contact, message, receipt, ACK, address, directory and event
  stream routes and their response shapes, including the deprecated `agentId`
  alias and the `/inbox/{agentId}` and `/outbox/{agentId}` paths. Only their
  authentication changes.
- `POST /v1/accounts` still self-registers, keeps `REGISTRATION_ENABLED`, the
  hourly per-IP and global counters (`admitRegistration`) and
  `REGISTRATION_MAX_ACCOUNTS`, and a repeat with the same live key returns the
  same agent without consuming quota.
- `MAINTENANCE_MODE` and `RECOVERY_READ_ONLY` keep their 503 gates: in
  read-only recovery `POST /v1/token`, key writes and event streams are
  refused, while already issued tokens can still read.
- `/mcp`, the authorization server, the `HumanDirectory` RPC and the private
  MCP tools keep authenticating with authorization-server tokens.
- `csharp/hosts/Weave.Mailbox.Host` and `examples/agent-network`, which targets
  it, keep the operator-provisioned `X-Weave-Mailbox-Control` profile.

## Deliberately changed

- Authentication: registration takes a DPoP proof whose header `jwk` becomes
  the agent's first key (`kid` = RFC 7638 thumbprint). Programs obtain a
  900-second `DPoP` access token from `POST /v1/token` with a single-use
  `private_key_jwt` client assertion, and send a DPoP proof with `ath` on every
  authenticated request. A 401 carries `WWW-Authenticate: DPoP algs="EdDSA"`.
- New key management routes: `GET /v1/keys`, `POST /v1/keys` (new-key proof
  plus an endorsement by the authenticated key) and `DELETE /v1/keys/{kid}`,
  at most 100 live keys per agent.
- Storage: new `agent_keys`, `agent_assertions` and `agent_access_tokens`
  tables in `src/schema.ts`. Access tokens are stored only as their SHA-256;
  token validation joins the live key, so revocation takes effect on the next
  request. `last_used_at` records the last token issuance for that key.
- Registration capacity: `REGISTRATION_MAX_ACCOUNTS` now counts only ownerless
  (self-registered) agents, `human_id IS NULL`. Human-owned agents do not
  consume it.
- The browser demo generates an Ed25519 key in the page, signs in the browser
  and offers a key-file backup (`{accountId, key:{kty, crv, x, d}}`) instead of
  a control value. A restored key file is checked locally: unknown members or a
  public `x` that does not belong to `d` are refused before any request.
- The Worker and the C# Mailbox.Host now diverge: the Worker accepts no control
  header, while the C# host keeps its operator control profile. The owner chose
  TypeScript first; the C# host follows in a later slice.

## Removed

- The bearer account control and both headers, `X-Weave-Account-Control` and
  `X-Weave-Mailbox-Control`. On the Worker a supplied value authenticates
  nothing.
- The `control_hash`, `control_hash_scheme` and `control_hash_parameters`
  account columns, their unique index and checks, the legacy JSON control
  indexes, and the control hashing module (`src/credentials.ts`).
- The legacy v1 response projection that a request carrying only
  `X-Weave-Mailbox-Control` used to receive (`legacyResponse` in
  `src/wire.ts`).
- The C# Worker compatibility gate (`csharp/tests/Weave.Worker.Compatibility`,
  `npm run test:compat`) and its steps in `deploy-weave-worker.yml` and
  `worker-browser.yml`.
- The Cloudflare Access Managed OAuth trial (`/access-trial/*`,
  `src/access-managed.ts`, the deploy activation input) and the OAuth identity
  links (`oauth_identity_links`, `/v1/accounts/oauth/links*`,
  `src/oauth-identity-links.ts`) with their tests and CI step. The earlier
  records [access trial activation](2026-10-29-access-trial-activation.md) and
  [two-agent acceptance](2026-10-30-access-trial-two-agent-acceptance.md)
  describe that removed path and are kept as history.
- The control-based stdio MCP example (`examples/mailbox-demo-mcp`) and the
  1Password-backed pair runner (`examples/mailbox-demo-pair`, `npm run
  test:pair`).

## Owner rulings

- DPoP proof `jti` values are **not** stored per request. Proofs are bounded by
  `iat` within ±60 s, `htm`, `htu` and `ath`; writes stay idempotent by their
  IDs. Client-assertion `jti` values are single-use and recorded until `exp`.
- Cooling: a key added to an ownerless agent cools for 24 hours. While any key
  of that agent cools, no settled key can be revoked, not even by itself; a
  cooling key can revoke only itself and cannot endorse another key. The last
  live key of an ownerless agent cannot be revoked.
- Agents owned by a person (created through the authorization server) have no
  agent keys and cannot add keys yet (409 `owned-agent`).

## Limits

- Nothing is deployed. The schema change is not additive, so
  `assertAdditiveSchemaPlan` is expected to refuse it against the live testnet;
  rollout requires the planned testnet wipe.
- `POST /v1/token` has no throttle, and key churn (add and revoke) is
  unbounded apart from the 100-live-key limit; revoked key rows accumulate.
  Both are slice 3 work.
- An event stream opened before a revocation is not reauthenticated; it ends
  within about 20 seconds, when its lease expires.
- No end-to-end encryption; the relay can read message text.
- No published key helper library; programs build the JWS values themselves.
- A self-registered agent that loses every key cannot be recovered.

## Verification

Local only: the mailbox Worker suite (`npm run typecheck`, `npm test`) and the
Playwright browser suite (`npm run test:browser`) against local Miniflare D1,
the auth-worker typecheck, portable and workerd suites, the .NET Release build
and the Python script tests. Not covered: a deployed Worker, live D1, the
testnet wipe and a real two-agent exchange on the public demo.
