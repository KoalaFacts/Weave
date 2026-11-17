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
  `client_name` (at most 128 characters, null when the client gave none or only
  whitespace). It is the name the consent page showed: a CIMD client's
  `client_name` from its metadata document, a DCR client's from its
  registration. The consent nonce Durable Object keeps that name from the page
  to the approval, so approval records it without fetching the document again
  (the SDK's `completeAuthorization` still resolves the document to check the
  redirect URI). It is self-reported: both workers replace control and bidi
  characters with `?`, the page escapes it and labels it "self-reported, not
  verified". The client ID is shown beside it.
- **`ownerOverview` returns, per agent,** up to 50 live grants (newest first),
  up to 20 live addresses (ID, label, public or private, whether it has an agent
  card) and up to 20 grants revoked in the last 30 days. The last list is for
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
  call (the D1 mirror read), which answers 401 `invalid_token`. Once SDK
  clean-up has run, a refresh attempt fails because the SDK grant is gone;
  until then a refresh may still return tokens, and `/mcp` refuses them. An
  agent in that state must not retry but stop and ask its owner to reconnect
  the client through consent. The agent guide says so (guide version 6).

## Removed

- The statements that grant revocation has no owner interface and that SDK
  refresh tokens are not revoked.

## Verification limits

- SDK clean-up is separate from the mailbox commit and can fail or stop at its
  bounds. Whenever it did not finish, the page reports access revoked (it is, at
  the resource) and shows that clean-up will be retried (`cleanup-pending`); an
  SDK failure also logs only the fixed category `grant-sdk-revoke`. This
  includes revoking a grant revoked more than 30 days ago, or one whose
  re-read finds the human frozen. Each load of `/account` retries for grants
  revoked in the last 30 days (at most 20 per agent). Per load it scans at most
  3 agents, taken in turn from a random start, and per agent at most 2 pages of
  100 SDK grants, and it makes at most 20 SDK revocations, which keeps a load
  well under the Workers limit of 1,000 KV operations. One agent's failure does
  not stop the others. If the retry on the visit that shows `cleanup-pending`
  finishes, that page says clean-up is complete instead. After 30 days a
  still-unfinished clean-up is no longer retried; the mailbox check keeps
  refusing the tokens, which expire inside the SDK on their own. The retry is
  idempotent and never un-revokes.
- Nothing records that a grant's SDK clean-up finished, so every load lists the
  SDK grants of up to 3 agents with a revocation in the last 30 days, even after
  their clean-up succeeded. Acceptable at demo scale; it costs KV reads only.
- The retry runs only when the owner loads the page; there is no background job.
- Client names, client IDs and address labels are self-reported and unverified;
  Weave does not confirm that a named client is what it claims.
- Revoking a client has no recent-sign-in check by design. For the other
  actions, GitHub offers no `max_age`, `prompt=login` or `auth_time`, so the
  check is an interactive account selection, not proof that credentials were
  re-entered.
- Verified in local Miniflare/workerd emulation only. No deployment, real OAuth
  issuer, real GitHub consent or live client was exercised.

## Owner app grants (2026-11-17)

Owner decision of 2026-11-17 (item AI-2 of the plan in agent-harness `projects/weave.md`): a person can let one app
manage every agent they own, including agents they own later: create agents, list them, rename them and act as any of
them for addresses, contacts, messages and proposals. Approving an external action stays the person's, on Weave's own
page, and keys and recovery codes stay on `/account`. The owner approved the new table that records these grants and
applying it to production at deploy time.

This is delivered in steps. Step 1 (this section) adds only storage and its release gate; nothing reads or writes the
table yet:

- **Table.** `owner_grants` mirrors one such consent grant per row (grant ID, human, client ID, the client's
  self-reported name, creation and revocation times), with an index on `human_id` and the same checks as
  `client_grants`. It holds no agent ID: the grant covers whatever agents the human owns at the time of each call.
- **Release gate.** Completed-only releases refuse any pending schema statement unless an owner-approved change covers
  it. The `external_action_keys` gate became one instance of `scripts/approved-schema-change.ts`, and
  `scripts/approved-owner-grants.ts` is the second, with its own default-off `apply_owner_grants_schema` input, its
  own workflow gates before release work and inside protected production, and its own deploy variable. With both on, one
  batch applies both; each pending statement must belong to an enabled change, in that change's order. Messages and
  behaviour of the `external_action_keys` opt-in are unchanged.
- **Owner deploy.** Until production has `owner_grants`, every completed-only release must be a manual production
  dispatch with `apply_owner_grants_schema: true` (and `apply_external_action_keys_schema: true` while that table is
  also missing); otherwise it stops before any change, as it does for any missing table.

Verification: `test/approved-owner-grants.test.ts` pins the statements to the generator and covers the combined plan;
`test/deploy.test.ts` runs opted-in completed-only releases on a real Miniflare D1 (owner_grants alone, both changes
in one batch, refusals with either opt-in missing, an unapproved index, ambiguous values, maintain mode and the OAuth
bootstrap); `test/deploy-entry.test.ts` checks the workflow input and gates. Not run against a deployment.

Step 2 makes the mailbox honour owner grants on `/mcp`; the authorization server does not issue them yet (step 3):

- **Recording.** `HumanDirectory.recordOwnerGrant` mirrors a grant for a human who is not frozen, at most 20 live per
  human (`grant-limit`), counted in the same transaction as the insert. `revoke-grant` (owner page) and
  `HumanDirectory.revokeGrant` revoke either kind of grant by its ID. `ownerOverview` lists the live owner grants
  (`ownerGrants`) and those revoked in the last 30 days (`revokedOwnerGrantIds`) for token clean-up.
- **Tokens.** An owner grant's claims carry `owner: true` and the human as both `userId` and `humanId`, never an
  account; an agent's claims stay as they were. Claims that mix the two, or a grant row of the other kind, are `401`.
- **Acting as an agent.** Every agent tool needs the `Weave-Agent` header naming an agent the grant's human owns at that
  moment; a missing, malformed, foreign or departed agent is the tool error `invalid_agent` (HTTP 400 before MCP framing),
  never a different agent. A token for one agent sending the header gets the same error. Each write re-checks, inside its
  own transaction, that the grant is live and the agent is still the human's (`grantCaller` now accepts either kind).
- **Managing agents.** `list_agents` (20 per page), `create_agent` and `rename_agent` need the new scope
  `weave:agents.manage`, are refused to agent tokens whatever scopes they list (`403 insufficient_scope`), take no
  header, and act only on the grant's human: another human's agent is not found. Creating still counts against
  `OWNED_AGENTS_PER_HUMAN` (`agent-limit`) and the hourly registration limits by source address (`capacity`); creating
  and renaming re-check the grant in their own transaction. Names follow the existing visible-name rule.
- **Not recorded.** As with an agent's grant, the mailbox does not log which grant made each call; messages and actions
  record their agent as before.

Verification: `test/owner-grants.test.ts` drives the production `/mcp` composition with Service Binding claims on a real
Miniflare D1 (recording and limits; list, create and rename on the human's agents only; acting as an owned agent, refusals
for a missing, foreign, unknown or departed agent; agent tokens refused management and the header; mixed claims, frozen and
revoked grants; revocation between authorization and each write), and `test/mcp-tool-contract.test.ts` checks the new
tools' results against their published schemas.

Step 3 makes the authorization server issue owner grants and lets the owner see and revoke them:

- **Consent.** A request whose scope includes `weave:agents.manage` gets its own consent page instead of the agent
  picker. It says the app may create, list and rename the person's agents and act as any of them, now or later, and may
  send an action only after the person approved it; and that it cannot approve actions, see or change keys and recovery
  codes, connect platform accounts or act as another person's agent. Its one approve form names `agent=all`. The approval
  is refused (400, nothing recorded) when `agent=all` comes with a request without that scope, or when that scope comes
  with a single-agent choice.
- **Issuing.** On approval the AS mirrors the grant with `HumanDirectory.recordOwnerGrant` before any code exists
  (`grant-limit`, frozen and other failures each answer a page and no code), then completes the SDK authorization with
  the human as the SDK user, the mailbox `grantId` in the grant metadata and props `{owner: true, humanId, grantId}`.
- **Owner page.** `/account` lists these apps under "Apps that manage all your agents" (escaped, self-reported names)
  with **Revoke access**, which needs no fresh sign-in, like an agent's client. Revoking also revokes the SDK grant held
  under the human; each later load retries that clean-up for owner grants revoked in the last 30 days, with the human
  counted as one more entry in the existing per-load limit.
- **Refusal codes.** `rename_agent` on an agent the human does not own is now the tool error `agent-not-found`. A
  problem response whose code had no title wrote `"title":undefined`, which is not JSON, so `/mcp` could not read the
  code: `agent-limit` was affected too. Codes without a title now omit it, and both codes have titles.

Verification: `test/workerd-owner-app-grant.test.ts` runs both Workers in Miniflare with fake GitHub: another person's
agent connected the usual way; both mismatched approvals refused with nothing recorded; consent for all agents; create,
list and rename; acting as the new agent and as one created after consent; refusals without a header, for the other
person's agent, and for a single-agent token naming an agent or managing agents; the account page listing the app; and
revocation ending the next call for every agent while the other person's client keeps working.
`test/workerd-owner-grants.test.ts` revokes an owner grant from `/account` and checks that the SDK grant is gone and the
refresh token gets `invalid_grant`. `test/consent.test.ts` and `test/owner-page.test.ts` check the pages' text, escaping
and forms; the mailbox `test/owner-grants.test.ts` checks the `agent-not-found` and `agent-limit` codes. Each was
checked by removing the guard it covers. Not run against a deployment.
