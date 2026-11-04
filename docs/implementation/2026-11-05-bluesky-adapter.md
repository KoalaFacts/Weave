# Bluesky: the first native publishing adapter

## Scope

Weave's first own adapter next to [Postiz](2026-11-04-postiz-publishing.md). It sits
behind the same [governed external actions](2026-11-04-worker-external-actions.md):
the owner approves an exact plan, dispatch happens at most once, and an uncertain
outcome is recorded as unknown. It talks to Bluesky directly through the AT Protocol
XRPC API of the `https://bsky.social` entryway, so a Bluesky post needs no
third-party subscription. Nothing was deployed and no real Bluesky account was used.

## Connect

On `/account`, **Connect Bluesky** (per agent) posts to `/account/bluesky`. The form
carries a signed `connect-bluesky` token, the handle and an app password, and it
requires a GitHub sign-in within the last 10 minutes. A stale session is sent to
GitHub and the password is dropped. The auth worker passes both values to the
mailbox's `connectBluesky` RPC and never stores, logs or renders them.

The mailbox then:

- accepts only an app password (`xxxx-xxxx-xxxx-xxxx`), so an account password is
  refused before any request and never reaches the vault;
- opens a session with `com.atproto.server.createSession`, and refuses a deactivated
  account;
- stores the credential `bluesky:<did>`, with the DID as target account, `@handle` as
  display, the app password sealed to that row and a fresh revision. Reconnecting the
  same account rotates the revision, so earlier approvals stop being executable.

## publish_post

Content is JSON `{text, media: [{mediaId, bytes, sha256, alt}]}`:

- at most 300 graphemes and 3000 bytes of text;
- up to 4 uploaded JPEG, PNG or WebP images of at most 1,000,000 bytes each, pinned
  by size and SHA-256, each with alt text of at most 2000 characters;
- text or media is required.

`list_my_credentials` describes this format for each Bluesky credential.

Links (`https://...`) and `#tags` become clickable facets at their UTF-8 byte
offsets. Mentions stay plain text.

## Dispatch

Each dispatch runs these steps in order:

1. `createSession` with the sealed app password. The session must be the approved
   DID.
2. `uploadBlob` for each image: the exact stored bytes with their stored type.
3. `createRecord` of `app.bsky.feed.post` into the account's repository.

Steps 1 and 2 publish nothing, so any failure there is a clean refusal:
`credential-refused`, `media-unavailable` or `media-upload-refused`. At step 3,
400/401/403/413/429 are refusals (`bluesky-refused-<status>`). Anything else, or a
response without the expected `at://<did>/app.bsky.feed.post/...` URI, is unknown and
never re-sent. The receipt is that AT URI. A Bluesky post is live at once, so it is
not tracked afterwards.

## Verification

- mailbox-worker `test/bluesky.test.ts`: connect runs in this process against a
  stubbed `createSession`; dispatch runs through the Worker with a fake Bluesky as its
  outbound service.
  - Connect stores the verified DID and sealed app password and rotates the revision
    on reconnect. An account-password-shaped value, a malformed handle or an extra
    field makes no request. A stale sign-in, another human, a wrong password or a
    deactivated account stores nothing.
  - Plan limits are checked, including multi-code-point graphemes. Facets cover a
    multibyte text.
  - A text post is created once as the verified account with link and tag facets, and
    the app password appears only in the session request.
  - Images reach `uploadBlob` as the exact files with their types and alt text.
  - GIF or unpinned media is refused at proposal.
  - A refused session, another account, and a create answered 400 or 429 all fail
    with nothing posted. A refused upload never creates the post. A 502 at create is
    unknown and never re-sent.
- Mutation checks: dropping the app-password shape check, the session-DID check, the
  refusal status list, UTF-8 byte offsets or the deactivated-account check each fail
  a test.
- auth-worker `test/workerd-owner-page.test.ts` (both Workers in workerd):
  - the owner connects through the form and the account is listed;
  - the password is never rendered, and a wrong password is `provider-refused`;
  - a stale sign-in, a missing password, or the field-less `/account` form store
    nothing.

  The portable form and page tests are updated for the new kind and result.

## Limits

- Only accounts hosted on bsky.social are supported: sessions go to the bsky.social
  entryway. A self-hosted PDS would need DID resolution to find its own endpoint.
- No video, link cards, quote posts, replies, threads or mentions yet.
- The app password is sealed in the vault; the owner revokes it in Bluesky or with
  Disconnect on `/account`.
