# A page for one action: app-linked approval (A2)

## Scope

An app such as Envoylet shows its owner what an agent wants to send. The owner still decides on Weave. The app
links to (or opens a window on) the authorization server's page for that one action:

```
<authorization server>/account/actions/<actionId>
```

On the demo, the authorization server is `https://weave-auth-demo.weave-389.workers.dev`. The app learns the outcome
from the action's status (`GET /v1/external-actions/{actionId}` with the agent's credential), never from a redirect.

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
- **No open redirect.** After a decision the page redirects to itself with `?result=approved`, `?result=rejected`
  or a failure reason, and tells the owner they can close it. It never redirects to the app.

## Verification

auth-worker workerd tests, against the real mailbox and authorization Workers:

- no session or a stale one signs in and returns to exactly this page, and a query is dropped;
- a malformed id gives 404; another method gives 405;
- only the linked action is shown, and its forms post back to the page;
- approving sends nothing until the agent executes, then the action is sent once; the other pending action is untouched;
- rejecting sends nothing;
- refused with no effect:
  - another action's token;
  - an unrelated owner-page token;
  - another human's session;
  - a cross-origin post.

Mutation checks: each of these fails a test:

- dropping the "this action only" check;
- dropping the fresh-sign-in gate on the page;
- dropping the page from the sign-in return allowlist.

## Limits

- The agent API does not return the page address. The mailbox does not know the authorization server's address
  unless first-party OAuth is enabled, so apps build it from the action id.
- Deciding in a chat channel without visiting Weave (A1) is not provided.
