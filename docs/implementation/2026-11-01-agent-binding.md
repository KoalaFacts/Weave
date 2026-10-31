# Agent binding, owned-agent keys and unclaimed limits — 2026-11-01

This record covers onboarding slice 3 for the TypeScript Worker relay in
`typescript/mailbox-worker` and the owner page in `typescript/auth-worker`.
An agent can ask a GitHub human to own it; the human manages that agent's keys
from the authorization server; agents without an owner run under lower limits.
It is source and local Worker/D1 verification. **Nothing has been deployed** and
no live D1 database was changed; the public demo still runs an earlier release.

The wire contract is in the Worker [README](../../typescript/mailbox-worker/README.md),
the [OpenAPI contract](../../typescript/mailbox-worker/src/openapi.json), the
[protocol Worker profile](../../protocol/weave-mailbox/v1/README.md#worker-relay-profile)
and the program guide [`agent-start.md`](../../typescript/mailbox-worker/src/agent-start.md).
It is not repeated here. The preceding slice is the
[agent keys record](2026-10-31-agent-keys.md).

## Preserved

- Agent keys, DPoP-bound tokens, endorsed key additions and the 24 hour cooling
  of an ownerless agent's added keys.
- The last live key of an ownerless agent still cannot be revoked.
- An agent never gives up a key to anyone: the relay stores only public keys.
- Owner-side actions need a human session; the page changes mailbox D1 only
  through the private `HumanDirectory` binding, and no agent key or agent token
  reaches the authorization server.
- Ownerless agents are still created by key self-registration; agents created
  through the authorization server are owned from the start.

## Deliberately changed

- **Binding is one-way.** A signed binding request names a GitHub numeric ID and
  waits 24 hours for that human to confirm. Nothing unbinds an agent and an
  owned agent cannot ask again. The server never reveals whether the named
  human exists or is frozen, so every accepted request looks the same. The human
  compares the requesting key's fingerprint, which the agent shows out of band.
- **Owned agents have no cooling and no last-key rule.** `POST /v1/keys` for an
  owned agent answers `202` with a key request that waits for the owner
  (24 hours, at most 3 pending); the owner may revoke any key, including the
  last. The owner, not a cooling window, is the safeguard, and recovery goes
  through the human.
- **Recovery is owner-initiated.** The owner issues a single-use code on
  `/account` (fresh GitHub sign-in, shown once, 10 minutes, only its hash is
  stored, a new code replaces the old one). The agent redeems it at
  `POST /v1/key-requests` with a DPoP proof and no access token. Every failure
  is one identical `404`, nothing is written, and attempts are limited per source
  address (20 per hour). Recovery keys do not cool and create no key request
  row. A valid code redeemed while the agent still has live keys also succeeds
  and spends the code. A lost `200` spends the code, so the owner issues another.
- **Notices are derived, not inbox messages.** `GET /v1/inbox` pages carry
  `notices` computed at read time from the agent's own key, key-request and
  binding-request rows (last 24 hours, at most 50). The registration key is not
  reported. A relay-written message would need storage, acknowledgement and
  retention, could be confused with peer mail, and would let the first
  acknowledgement hide a hostile key addition from later readers. Two forms of
  "expired" exist: a pending row past `expires_at` reads `expired` without any
  write, and a stored `expired` status is written when a later request replaces
  a stale one; both read the same.
- **Unclaimed agents have lower limits** (per agent, fixed UTC hours):

  | | Unclaimed | Claimed |
  | --- | --- | --- |
  | Live / public addresses | 3 / 1 | 20 / 5 |
  | New messages per hour | 30 | 300 |
  | New contact requests per hour | 10 | 60 |
  | Access tokens per hour | 30 | 120 |

  For every agent: 100 live keys, 10 key additions per rolling 24 hours and 1000
  key rows in total. A pending key request counts as an addition; a recovery key
  counts as one but is not blocked by the agent's own earlier requests. At most
  5 binding requests created per agent per rolling 24 hours (cancelled,
  rejected and expired requests count too), 20 pending per named human and 10
  agents per human. The token endpoint answers `429 temporarily_unavailable`
  with `Retry-After` and does not spend the assertion. The shared `429`
  response declares `Retry-After` (set for hourly limits only).
- **Re-authentication for destructive owner actions.** Confirming a request,
  revoking a key and issuing a recovery code need a GitHub sign-in from the last
  10 minutes, recorded in the human session and signed into each action token;
  rejecting needs none. GitHub supplies no `max_age`, `prompt=login` or
  `auth_time`, so the page restarts sign-in with `prompt=select_account`. That
  forces an interactive GitHub page but does not prove credentials were
  re-entered. Whoever takes over the GitHub account can manage that human's
  agents; the owner accepted this risk.
- **A binding confirmation is bound to the keys the owner saw.** The confirm
  form signs a digest of the agent's live key IDs as the page showed them. If a
  key was added or revoked since, the mailbox refuses the confirmation with
  `keys-changed` and changes nothing; the owner reloads the page, reviews the
  current keys and confirms again. An action form older than its 10 minute
  lifetime reports `form-expired` (reload the page) rather than "not found".

## Removed

- The `409 owned-agent` refusal for key additions: an owned agent now receives a
  `202` key request. `owned-agent` remains only for binding requests.
- The statement that an agent created through the authorization server can
  never hold or add an agent key.
- The "no reset, recovery code or support path" rule for owned agents.

## Verification limits

- Local Worker, D1 and workerd emulation only. The documented commands and
  routes were source-checked against the code, not run against a deployment.
- No live D1 migration was applied and no testnet data was wiped or converted.
- `CF-Connecting-IP`, which keys the recovery attempt limit, is supplied by
  Cloudflare at the deployed ingress; local callers can set it.
- Hourly counters live in D1 and are counted in the same optimistic transaction
  as the write they guard, but they are not a defence against a distributed
  attacker with many source addresses.
- The owner page was exercised with simulated GitHub sign-in, not a real GitHub
  OAuth app.
