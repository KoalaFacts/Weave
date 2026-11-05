# External-action status notices on the inbox

## Scope

Shared plan step 4 on [#365](https://github.com/KoalaFacts/Weave/issues/365) asks for a status-change notification, so
that apps need not poll each action. This is the first increment. It needs no schema change and no new endpoint.

Every `GET /v1/inbox` page already carries `notices`: changes to the agent from the last 24 hours, derived from its own
rows. Notices now also cover the agent's **external actions**. One inbox read shows the current status of the actions
the agent proposed, decided, executed or saw expire in the window, within the shared cap of 50 notices. Before, an app had to poll
`GET /v1/external-actions/{actionId}` for each action it was tracking, and knew nothing about an action whose ID it had
lost.

## Behaviour

- **Shape.** Each notice is:

  ```json
  {"kind": "external-action", "actionId": "…", "status": "approved",
   "credentialRef": "…", "operation": "publish_post", "createdAt": "…",
   "expiresAt": "…", "decidedAt": "…", "completedAt": null}
  ```

  `status` is the value `GET /v1/external-actions/{actionId}` returns: `pending`, `approved`, `rejected`, `expired`,
  `dispatching`, `completed`, `failed` or `unknown`. A pending or approved action past `expiresAt` reads as `expired`.
  Both read it through one function, `effectiveStatus` in `src/external-actions.ts`, so the two cannot drift.
- **One per action.** Each action appears once, with its latest status, not once per transition.
- **Ordering and window.**
  - An action's time is its last write, or its expiry when a live plan lapsed with nothing written.
  - It is reported when that time falls in the last 24 hours.
  - It is merged into the existing notices, newest first, at most 50 in all.
- **No content.** The planned content, the receipt and the failure text are not in the notice. The app reads
  `GET /v1/external-actions/{actionId}` for those, with the same credential. This keeps the inbox page bounded and adds
  no data the agent could not already read.
- **Own actions only.** Only rows with the caller's `account_id` are read. Another agent's actions never appear.
- **Lost proposals.** A proposal whose HTTP response was lost (the case behind Envoylet's `propose-idempotency`
  request) shows up as a `pending` notice with its `actionId`. The app can then show the owner the plan, or let it
  expire. This does not replace `Idempotency-Key` (#366, waiting on an owner decision). It does let an app find the
  orphan plan without a second proposal.
- **Unchanged:**
  - the MCP `list_my_inbox` result, which still has no `notices` field;
  - the inbox SSE stream, which still sends messages only;
  - every action endpoint.

## Verification

`test/external-action-notices.test.ts` runs against the real Worker and D1 in Miniflare: the propose, owner-decide and
execute paths, with a fake provider. Twelve tests:

- `inbox_ProposedAction_PendingNoticeWithoutContent`: exact shape; the content string never appears.
- `inbox_ApprovedAndExecuted_OneNoticeWithLatestStatus`: one notice per action, `approved` then `completed`, and
  `completedAt` matches the execute response.
- `inbox_UnknownOutcome_NoticeReportsUnknown`.
- `inbox_ProviderRejected_NoticeReportsFailed`.
- `inbox_RejectedAction_NoticeReportsRejected`.
- `inbox_PendingOrApprovedPastExpiry_NoticeReportsExpired`.
- `inbox_ExpiryAfterLastUpdate_NewestFirstByExpiry`.
- `inbox_ExpiredInWindowAfterOlderLastChange_StillReported`: proposed 24.5 h ago, lapsed 23.5 h ago.
- `inbox_ActionOutsideWindow_NotReported`.
- `inbox_AnotherAgentsAction_NotReported`.
- `inbox_ManyActions_CappedAtMaxNoticesNewestFirst`: 56 actions in, 50 out.
- `inbox_LapsedPlanNewerThanCappedRows_RankedByExpiry`: with the cap full, a plan last written before every other row
  but lapsed after them is still ranked by its expiry.

The first nine were written before the change and all failed then, each because the inbox returned no `external-action` notice. The other three were added with the change and the independent review; each fails under a mutation below or covers a
further status.

Mutation checks. Each mutation made at least one test fail, and each was then reverted:

| Mutation | Tests failing |
| --- | --- |
| Drop the "expired inside the window" clause | 1 |
| Report the stored status instead of `expired` | 3 |
| Drop the `account_id` filter | 1 |
| Sort oldest first | 1 |
| Time an expired action by its last write | 1 |
| Rank rows in SQL by their last write instead of their event time | 1 |

The OpenAPI `Notice` schema gains the `external-action` variant. The inbox page's `notices` description, Agent Start
and the Worker README are updated.

## Limits

- **Still a pull.** This is not push: an app reads the inbox (one request covers all its actions) instead of each
  action. A push to the app (a webhook) needs somewhere to keep the app's callback address. That is a schema change and
  needs an owner decision first.
- **Publication progress.** A Postiz post's later progress (`queued` to `published`) is recorded on the publication,
  not the action, so it produces no notice. Read the action for it.
- **Row scan.** No index covers `updated_at`, so each inbox read scans all of this agent's action rows (through the
  existing `account_id` index). Those rows are never deleted, so the cost grows with use. An index would be a schema
  change and is left for when volume calls for it.
- **The window and the cap.** Notices cover 24 hours and share the cap of 50 with key and binding notices, so a busy
  agent can see only its most recent actions there. An action that changed earlier, or was pushed out, is read directly.
- **Deploy.** It reaches the demo only through the owner's usual release. That release is `completed-only`, with no
  schema change.
