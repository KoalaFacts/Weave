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
- **No disclosure.** An action that is decided, expired or another human's shows the same page: "not waiting for
  your approval", with no forms.
- **No open redirect, no forgeable outcome.** A decision is answered directly with what that request just did
  (approved, rejected or the failure reason), and the owner is told they can close the page. Nothing is redirected to
  the app. The page never reads an outcome from its URL, so a link carrying `?result=approved` or `?result=rejected`
  cannot make it claim a decision that was not made.

## Verification

auth-worker workerd tests, against the real mailbox and authorization Workers:

- no session or a stale one signs in and returns to exactly this page, and a query is dropped;
- a malformed id gives 404; another method gives 405;
- only the linked action is shown, and its forms post back to the page;
- approving sends nothing until the agent executes, then the action is sent once; the other pending action is untouched;
- rejecting sends nothing;
- a forged `?result=` claims no outcome on a pending or an approved action;
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
- returning `approvalUrl` without the first-party Service Binding (mailbox unit test).

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
or stamping `completed_at` on approve or reject each fails a test. Stamping it on the `dispatching` write is an
equivalent mutant on every path a test can reach: the outcome write overwrites it, and only an outcome that could not
be recorded would show it.

## Limits

- A relay without first-party OAuth returns `approvalUrl: null`: it has no Weave owner pages.
- Deciding in a chat channel without visiting Weave (A1) is not provided.
