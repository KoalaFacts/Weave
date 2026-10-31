# Owner page: connected clients and addresses — 2026-11-01

This record covers onboarding slice 5 in `typescript/mailbox-worker` and
`typescript/auth-worker`. A signed-in owner sees, per agent, the MCP clients
connected to it, its keys and its addresses, and can disconnect a client. It is
source and local Worker/D1 verification. **Nothing has been deployed.** The
documented commands and flows were source-checked, not run against a
deployment. It builds on the [MCP record](2026-11-01-mcp-a2a-messages.md) and the
[agent binding record](2026-11-01-agent-binding.md).

## Preserved

- The mailbox `client_grants` mirror stays the enforcement point: every private
  `/mcp` call reads it and refuses a revoked, mismatched or missing grant.
- `HumanDirectory.revokeGrant` is unchanged, and `HumanDirectory` gains no
  method: grants and addresses ride `ownerOverview`, revocation rides
  `ownerAction`.
- Confirm, revoke-key and issue-recovery-code still need a GitHub sign-in from
  the last 10 minutes; reject does not. Every form still carries a signed
  10 minute action token.
- A frozen human sees only the frozen page and cannot act.

## Deliberately changed

- **Client grants carry the client's name.** `client_grants` gains
  `client_name` (at most 128 characters, null when the client gave none), taken
  from the CIMD/DCR `client_name` at consent. It is self-reported: the page
  escapes it, strips control characters to `?` and labels it "self-reported,
  not verified". The client ID is shown beside it.
- **`ownerOverview` returns, per agent,** up to 50 live grants (newest first),
  up to 20 live addresses (ID, label, public or private, whether it has an agent
  card) and up to 20 grants revoked in the last 24 hours. The last list is for
  the authorization server's clean-up only and is never rendered. Address labels
  are agent-supplied and marked unverified on the page.
- **`revoke-grant` is a new owner action** for the same human's grant only
  (another human's grant ID is `not-found`; an already revoked grant is `ok`).
  It is deliberately a protective action and does not need the recent GitHub
  sign-in: the worst a stolen session can do with it is disconnect the owner's
  own clients. Its form member set is exactly `grantId`.
- **The AS records the mailbox `grantId` in the SDK grant's metadata** at
  consent. After the mailbox commit it lists the agent's SDK grants (paged) and
  revokes those whose metadata matches, which also ends their refresh tokens.
- **Effect on the client.** At the resource the revocation applies on the next
  call (the D1 mirror read), which answers 401 `invalid_token`; a refresh
  attempt fails because the SDK grant is gone. An agent in that state must stop
  and ask its owner to reconnect the client through consent. The agent guide
  says so (guide version 6).

## Removed

- The statements that grant revocation has no owner interface and that SDK
  refresh tokens are not revoked.

## Verification limits

- SDK clean-up is separate from the mailbox commit and can fail. The page then
  reports access revoked (it is, at the resource), logs only the fixed category
  `grant-sdk-revoke` and shows that clean-up will be retried. The next load of
  `/account` retries for grants revoked in the last 24 hours, at most 20 SDK
  revocations per load and at most 1,000 SDK grants scanned. After 24 hours a
  still-failing clean-up is no longer retried; the mailbox check keeps refusing
  the tokens, which expire inside the SDK on their own. The retry is idempotent
  and never un-revokes.
- The retry runs only when the owner loads the page; there is no background job.
- Client names, client IDs and address labels are self-reported and unverified;
  Weave does not confirm that a named client is what it claims.
- Revoking a client has no recent-sign-in check by design. For the other
  actions, GitHub offers no `max_age`, `prompt=login` or `auth_time`, so the
  check is an interactive account selection, not proof that credentials were
  re-entered.
- Verified in local Miniflare/workerd emulation only. No deployment, real OAuth
  issuer, real GitHub consent or live client was exercised.
