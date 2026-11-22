# A page for one action: app-linked approval (A2)

## Scope

An app such as Envoylet shows its owner what an agent wants to send. The owner still decides on Weave. The app
links to (or opens a window on) the authorization server's page for that one action:

```
<authorization server>/account/actions/<actionId>
```

On the demo, the authorization server is `https://weave-auth-demo.weave-389.workers.dev`. The app learns the outcome
from the action's status (`GET /v1/external-actions/{actionId}` with the agent's credential), never from a redirect.

The action itself carries this address as `approvalUrl` (HTTP API and the MCP action tools), so an app does not need to
know the authorization server. It is set only while the action is pending, and only on a relay with a first-party
authorization server: one reached through the `AUTH_SERVER` Service Binding at an HTTPS `OAUTH_ISSUER`. A third-party
issuer (introspection) hosts no such page, so it is null there, as it is once the action is decided or expired.

This was chosen over letting the app approve on the owner's behalf, option A1 in the owner's discussion. A2 keeps
the existing guarantee: Weave itself verifies the human, so approval does not depend on the app's own security. A1
remains possible later for chat channels.

## Behaviour

- **Sign-in.**
  - With no session, the page sends the owner to GitHub and returns to exactly this page.
  - Approving needs a sign-in in the last 10 minutes, so a stale session signs in again *before* the page is shown,
    not after the owner clicks.
  - The return address is always the bare page. A query string never travels through sign-in, and the sign-in
    return allowlist admits only this exact path shape besides `/authorize` and `/account`.
- **Same decision as the owner page.** The page shows the same exact plan, provider-verified account, credential
  revision and file previews, and the same signed forms bound to the plan digest. The mailbox still enforces
  ownership, the fresh sign-in, the plan digest and at-most-once dispatch.
- **Only this action.** A POST to the page decides only that action. These are refused as `not-found`, with no
  effect:
  - a signed form for another action;
  - any other owner-page form, such as a recovery code;
  - a form signed for another human.
- **No disclosure.** An action that is another human's, or unknown, shows the same page: "not waiting for your
  approval", with no forms.
- **What became of it (2026-11-18).** The owner who opens the link of their own agent's action once it no longer waits
  sees its status, read from the mailbox, never from the URL, and no forms: approved (nothing is sent until the agent
  sends it, once, before it expires), rejected, expired without being sent, being sent (`dispatching`: never sent
  again), sent, not sent (with the stored failure code, escaped) or unknown (never sent again; check the platform).
  "Own" is the agent's current owner, as for deciding: once an agent passes to another human, its actions are no
  longer described to the previous one. The mailbox RPC `ownerPendingAction` adds `settled: {status, failure} | null`;
  an auth Worker reading a mailbox without it shows the old sentence.
- **Where it went (2026-11-18).** A sent action also shows the platform's reference for it (the stored receipt, escaped)
  and, for an effect the platform finishes later and Weave tracks (a queued or scheduled Postiz post), whether the
  platform has not reported it published yet (promising no further check, since tracking stops after 7 days, on a
  revoked credential or for an adapter that cannot report it), published it, or reported a failure (its stored reason, escaped); a published
  post's stored URL is a "View it on the platform" link (`rel="noopener noreferrer"`) only when it is an absolute
  `https://` URL, as the store already requires. `settled` gains `receipt` and `publication`, both null unless the
  status is `completed`; an auth Worker reading a mailbox without them shows "This action was sent." alone. Reading
  sends and re-checks nothing.
- **Why it was not sent (2026-11-18).** A not-sent action's page follows the stored failure code with what it means and
  what the owner can do: the five re-checks at send time (`owner-changed`, `owner-frozen`, `credential-changed`,
  `credential-unreadable`: disconnect and connect the account again; `plan-changed`), the platform's refusal of the
  account's sign-in (`provider-rejected: credential-refused`: connect again) or of a file no longer stored as approved
  (`provider-rejected: media-unavailable`), and any other `provider-rejected: <reason>` as "the platform refused it, so
  nothing was posted". A code the page does not know, and an inherited property name, adds nothing: never guessed. The
  text is fixed in the auth Worker; the mailbox RPC is unchanged.
- **Which action it was (2026-11-18).** The page of the owner's own settled action, whatever became of it, also says what
  the action was: the agent (its self-reported name, marked unverified), the operation and provider, the account the plan
  bound (the provider-verified ID, with the credential's name for it only while that credential still points at that
  account, revoked or not) and the content exactly as proposed, all escaped. `settled` gains
  `about: {agentId, agentDisplayName, provider, operation, targetAccount, targetDisplay, content}`; an auth Worker reading a
  mailbox without it, or a malformed one, shows the status alone. Another human still learns nothing; as for the status,
  "own" is the agent's current owner, so a new owner also sees what the agent proposed before it passed to them.
- **Withdrawing an approval (2026-11-18).** The page of the owner's own approved action that the agent has not sent yet
  offers "Withdraw approval" (a `reject-action` form signed for that action, posting back to the page). The mailbox's
  `reject-action` now also takes an approved, unexpired action that execute has not claimed and makes it `rejected`,
  re-stamping `decidedAt` and `decided_by` with the withdrawal; execute then returns it unchanged and sends nothing. Once
  execute has committed `dispatching`, the withdrawal is `not-pending` and the page says the action no longer waits; an
  approval past its expiry stays expired. Like rejecting, it is protective and needs no fresh sign-in at the mailbox (the
  page itself requires one to open). The optimistic transaction serializes it against execute's claim, so exactly one
  wins. A Reject form signed while the action was pending (on `/account` or this page, within the form's lifetime) now also
  withdraws it once approved: protective, since it can only stop a send. The agent learns it as status `rejected` (and its inbox notice); OpenAPI's status and `decidedAt` descriptions
  and `agent-start.md` say an approval can be withdrawn before execute.
- **By when an approval must be used (2026-11-18).** The page of the owner's own approved, unsent action also says the time
  by which the agent must send it (the plan's expiry) and that after it the action expires and is never sent, so an owner
  deciding whether to withdraw knows how long the approval stands. `settled` gains `sendBy`, the plan's `expiresAt` while
  the action is approved and unsent, otherwise null; an auth Worker reading a mailbox without it, or a malformed one, adds
  nothing. No schema or agent API change.
- **A link to the sent post (2026-11-22).** The page of the owner's own sent action now also offers "View the post on
  the platform" (`rel="noopener noreferrer"`) when the stored receipt alone names the post's own address: native X
  (`https://x.com/i/web/status/<id>`), Bluesky (`https://bsky.app/profile/<did>/post/<rkey>` from the post's AT-URI),
  LinkedIn (`https://www.linkedin.com/feed/update/<urn>/`) and YouTube (`https://www.youtube.com/watch?v=<id>`). Each
  adapter derives it from a receipt in exactly the shape it confirms when sending (`postUrl` on the adapter); any other
  shape, and Discord, Slack, Teams and Postiz (whose addresses need more than the receipt, or which Weave tracks
  separately), give no link. A Telegram channel or supergroup message (Bot API chat ID `-100…`, receipt
  `<chatId>/<messageId>`) links to `https://t.me/c/<channel>/<messageId>`, which opens for the chat's members, the owner
  included (2026-11-22, #525); a basic group's message has no link. `settled` gains `postUrl`, null unless the status is `completed`; the page links it
  only as an absolute `https://` URL, escaped, and an auth Worker reading a mailbox without it adds nothing. Reading sends
  nothing; no schema or agent API change.
- **Recent actions on `/account` (2026-11-18).** The owner page lists, under "Recent actions", the actions of the agents
  the human owns now that stopped waiting in the last 7 days, newest change first, at most 20: decided, being sent, sent,
  not sent (with the stored failure code) or unknown by their last change, and a plan left pending past its expiry, by
  that expiry, as expired. Each links to its own page above, which shows the rest. The mailbox overview adds
  `recentActions: {actionId, agentId, agentDisplayName, provider, operation, status, failure, changedAt}[]`; an auth
  Worker reading a mailbox without it lists none. Everything shown is escaped; another human's actions are never listed.
- **Saying when a list left some out (2026-11-18).** `/account` shows at most the 200 oldest pending actions and the 20
  most recent outcomes, and used to read as the whole list when there were more (likely with many owned agents). The
  mailbox overview now reads one more of each and adds `moreExternalActions` and `moreRecentActions`; when true the page
  says under that list that only the oldest N (or most recent N) are shown and that each action's own page, linked from
  the app that proposed it, still works. A mailbox without the flags adds no note.
- **Warning before disconnecting an account with waiting actions (2026-11-18).** Each connected account on `/account`
  that the owner's waiting actions use says, above its Disconnect button, how many of that agent's actions waiting for
  approval use it and that disconnecting it means they can never be approved or sent (the confirm would answer
  `account-changed`). It counts the shown pending list only: an action of another agent, on another credential, or
  already flagged `accountChanged` is not counted, and a cut list says "at least". Approved actions not yet sent are not
  in that list and not counted. No mailbox or agent API change.
- **No open redirect, no forgeable outcome.** A decision is answered directly with what that request just did
  (approved, rejected or the failure reason), and the owner is told they can close the page. Nothing is redirected to
  the app. The page never reads an outcome from its URL, so a link carrying `?result=approved` or `?result=rejected`
  cannot make it claim a decision that was not made.

## Verification

auth-worker workerd tests, against the real mailbox and authorization Workers:

- recent actions (2026-11-18): once approved and sent, the action leaves the waiting list and `/account` lists it as sent,
  linking to its page, for its owner only. Mailbox tests pin the 7-day window at the exact tick, newest-first order, the
  20 cap, the read's own clock, a pending plan only from its expiry, and other and former owners; nine mutants (owner
  filter, order, window edge, future changes, the expiry branch, the failure only on `failed`, escaping, the missing
  list, the link) each fail a test;
- lists that left some out (2026-11-18): with 21 outcomes `/account` lists 20 and says only the 20 most recent are
  listed; with exactly 20 it says nothing; another human sees no note. Mailbox tests pin both flags at the exact cap
  (200 pending, 20 recent) and one over; mutants (`>=` for either flag, no extra row read for either list, no slice for
  either list, the page's note shown for a missing flag, the pending note keyed on the recent flag, the recent note
  counting the pending list) each fail a test;
- by when an approval must be used (2026-11-18): after approving, the page says the agent must send it by the
  proposal's own `expiresAt`; the mailbox test pins `sendBy` to that time for approved only (null for every other
  status, for an approval past expiry and for another human); page tests pin escaping, other statuses and a missing or
  malformed `sendBy`. Mutants (`sendBy` always the expiry or always null in the mailbox; on the page the status check
  dropped, the type check dropped, no escaping, the line not shown) each fail a test;
- no session or a stale one signs in and returns to exactly this page, and a query is dropped;
- a malformed id gives 404; another method gives 405;
- only the linked action is shown, and its forms post back to the page;
- approving sends nothing until the agent executes, then the action is sent once; the other pending action is untouched;
- rejecting sends nothing;
- a forged `?result=` claims no outcome on a pending or an approved action;
- the owner's own action reads "This action was sent." after one execute, "This action was rejected." after Reject,
  and approved, failed (reason escaped), unknown and expired with what each means, with no forms and nothing sent;
  another human still reads "not waiting for your approval" for it;
- after one execute the page also shows the exact content and "as Desk Alpha (`urn:li:person:alpha`)"; another human
  sees neither;
- after one execute the page shows the fake provider's receipt as the platform's reference, and with a tracked
  publication recorded, "The platform published it." with its https link; another human sees neither;
- the proposal's `approvalUrl` is exactly this page, opening it shows that action, and it becomes null once decided
  (the mailbox Worker bound to the authorization Worker, as deployed);
- refused with no effect:
  - another action's token;
  - an unrelated owner-page token;
  - another human's session;
  - a cross-origin post.

Mutation checks: each of these fails a test:

- dropping the "this action only" check;
- dropping the fresh-sign-in gate on the page;
- dropping the page from the sign-in return allowlist;
- returning `approvalUrl` after a decision;
- returning `approvalUrl` without the first-party Service Binding (mailbox unit test);
- describing a settled action to a human who does not own its agent, reading the stored status instead of the
  expiry-aware one, showing the failure code unescaped, or dropping the settled text
  (`ownerPendingAction_OwnActionNoLongerPending_SettledWithStatusAndFailureOnlyForItsOwner` and the workerd tests above).
  Equivalent: the guard that a still-pending row is not described as settled (a live pending action of the owner is
  always returned for decision first).
- explaining a failure code for an action that was not `failed`, dropping or swapping any explanation, explaining an
  unknown code or an inherited property name, or giving the generic platform refusal for a bare `provider-rejected: `
  (`actionPage_FailedWithKnownCode_ExplainsWhatHappenedAndWhatToDo` and the workerd test
  `actionPage_OwnActionApprovedFailedOrUnknown_SaysWhatBecameOfIt`).
- returning the receipt or the publication for an action that was not sent, showing either on the page for another
  status, linking a URL that is not absolute `https://` (`javascript:`, `http:`, protocol-relative), or showing the
  receipt, the publication's error or its URL unescaped (`ownerPendingAction_OwnActionNoLongerPending_…` and
  `actionPage_SentWithReceiptAndPublication_ReferenceStateAndHttpsLinkOnly`).

- a link to the sent post (2026-11-22): mutants (each adapter's receipt check dropped, `postUrl` returned for a status
  other than `completed`, the page's https check or escaping dropped, the page link shown for another status) each fail a
  test (`postUrl_ReceiptInTheConfirmedShape_PlatformAddressElseNull`, `youtubeUpload_postUrl_VideoIdOnlyBecomesTheWatchAddress`,
  `ownerPendingAction_SentNativePost_PostUrlFromTheReceiptOnlyWhenSent`,
  `actionPage_SentWithReceiptAndPublication_ReferenceStateAndHttpsLinkOnly` and the workerd test, which reads a native X
  receipt from the mailbox's stored row and shows its link to the owner only). Telegram (#525): mutants (lower or upper
  channel bound dropped, message ID 0 or leading zero allowed, end anchor dropped, the raw `100…` ID linked, `postUrl`
  removed) each fail `postUrl_ReceiptInTheConfirmedShape_PlatformAddressElseNull`; widening the ID to 9–13 digits is
  equivalent (the lower bound still refuses a basic group).

- withdrawing an approval (2026-11-18): refusing it for an approved action (the old pending-only check), withdrawing an
  approval past its expiry, offering the withdrawal on the page for any other settled status, or dropping the page's
  `not-pending` explanation each fails a test (`rejectExternalAction_ApprovedNotYetSent_WithdrawsSoNothingIsEverSent`,
  `rejectExternalAction_ApprovedButAnotherHumanOrExpiredOrDispatching_NothingChanges`,
  `actionPage_SettledApprovedWithToken_OffersWithdrawalOnlyThen` and the workerd tests
  `actionPage_WithdrawApprovalBeforeSending_RejectedAndNeverSent` and `actionPage_WithdrawAfterAgentSent_NotPendingAndSentOnce`,
  which also show that a withdrawal posted after the agent sent changes nothing and the action is sent once). Equivalent:
  minting the form token for every settled status (the page renders the form only for `approved`), and binding the update
  to the read status instead of `IN ('pending','approved')` (the transaction is serialized).

- dropping the settled action's description, showing its content unescaped, naming the account with a credential that
  now points elsewhere, showing a null name, accepting a malformed description, or describing an unlisted status
  (`ownerPendingAction_OwnSettledAction_DescribesThePlanAndTheAccountNameOnlyWhileTheCredentialStillPointsThere` and
  `actionPage_SettledWithPlan_SaysWhichActionItWasEscaped`).

The mailbox's owner-decision RPC (`ownerAction`) checks the shape of every decision before reading anything. A
malformed `planDigest` on a pending action, or a malformed `keysDigest` on a pending binding (not a string or
not 43 base64url characters; a missing `planDigest` too), is `not-found` with no effect, not `plan-changed` or `keys-changed`, and the
well-formed digest then decides it (mailbox tests). Dropping either digest-shape check fails a test. The scanned ID-shape checks
(human, request, agent and kid) are equivalent mutants: a malformed ID is never found by the
ownership lookup that follows, so it is `not-found` either way.

`revoke-grant` and `revoke-credential` are shape-checked the same way: a missing or non-string grant ID, a missing or
malformed agent ID, and a credential reference that is missing, not a string, empty, over 64 characters or outside
`[A-Za-z0-9._:-]` are `not-found` and revoke nothing, and the well-formed action then revokes the same grant or
credential (mailbox test `ownerAction_MalformedRevokeGrantOrCredential_NotFoundAndNothingRevoked`). Without these checks a
missing value was no longer answered `not-found`, and a stored reference outside the adapter pattern was revoked. The scan also confirmed that existing tests kill dropping each kind from the
fresh-sign-in set, the future-skew check, both window boundaries (`>` to `>=`), the read-only/maintenance refusal and
the unknown-kind refusal. Equivalent, left unpinned: a case-insensitive grant ID (stored IDs are lowercase and the
lookup compares exactly) and dropping the object check (a non-object has no `humanId`).

The action's times, which Envoylet shows on its plan card (mailbox tests `externalActionTimes_*`, #449): `decidedAt` is
set only by the owner's approve or reject (also a reject past expiry) and keeps that value through every execute;
`completedAt` is set exactly when the action reaches `completed`, `failed` (including a refusal before dispatch, which
leaves `dispatchedAt` null) or `unknown`, is kept on every repeated execute, and stays null while `pending`, `approved`,
`rejected` or `expired`. Stamping `decided_at` on the outcome write, leaving `completed_at` off a pre-dispatch refusal,
or stamping `completed_at` on approve or reject each fails a test. `dispatchedAt`, which Envoylet shows as "Weave
started sending" (Envoylet #154), is stamped when the action enters `dispatching`, before the platform answers, and is
kept on every read and repeated execute for `completed`, `failed` and `unknown`; the tests make the fake platform
answer 25 ms late, so an outcome write that restamped `dispatched_at` (and returned it) fails them, where it passed the
earlier `>=` checks. Stamping `completed_at` on the `dispatching` write is an
equivalent mutant on every path a test can reach: the outcome write overwrites it, and only an outcome that could not
be recorded would show it.

## Limits

- A relay without first-party OAuth returns `approvalUrl: null`: it has no Weave owner pages.
- Deciding in a chat channel without visiting Weave (A1) is not provided.
