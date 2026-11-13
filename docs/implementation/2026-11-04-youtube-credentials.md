# YouTube: owner-connected credentials and governed uploads

## Scope

Builds on [governed external actions](2026-11-04-worker-external-actions.md). An owner
connects YouTube for one of their agents from `/account`; the resulting credential is
what a YouTube upload is approved for. No real Google project was used and nothing
was deployed.

## Flow

1. `/account` shows **Connect YouTube** per owned agent when the auth worker has
   `GOOGLE_CLIENT_ID`. The form token is bound to the human and agent and needs a
   GitHub sign-in in the last 10 minutes.
2. The auth worker redirects to Google with PKCE, `access_type=offline`,
   `prompt=consent select_account` and exactly the scopes `youtube.upload` and
   `youtube.readonly`. The pending connection (state, verifier, human, agent) is a
   signed `__Host-weave_connect` cookie for 10 minutes.
3. `GET /connect/youtube/callback` accepts only the same signed-in human and the
   matching state, then calls the mailbox RPC `connectCredential`.
4. The mailbox re-checks the sign-in window, that the human owns the agent and is not
   frozen (before the code is exchanged and again in the write), exchanges the code,
   requires both scopes and a refresh token, and reads `channels?mine=true`. Exactly
   one channel must come back; its ID (`UC…`) is the target account and its title the
   display name.
5. The refresh token is sealed (AES-256-GCM, `EXTERNAL_CREDENTIAL_KEY`) with additional
   data binding agent, credential reference (`youtube`), provider and revision. Every
   connect writes a new random revision, so actions approved under an earlier
   revision fail with `credential-changed` instead of running.

**Disconnect** (`revoke-credential`) needs no fresh sign-in, erases the sealed secret
and marks the row revoked; a schema check keeps revoked rows secret-free.

## Upload

1. The agent stores the video with `PUT /v1/external-media` (raw body, `video/mp4`,
   `video/quicktime` or `video/webm`, exact `Content-Length`, at most 100 MiB). It is
   written once to R2 (`EXTERNAL_MEDIA`) under `<agent>/<random mediaId>`; the response
   gives the SHA-256 computed from what R2 stored.
2. It proposes `upload_video` through credential `youtube` with content
   `{title, description, privacyStatus, mediaId, bytes, sha256}`. The plan is refused
   unless it names the agent's own media with its exact size and SHA-256, and the
   title and description fit YouTube's limits. The owner approves that JSON verbatim,
   so the approval pins the file, the metadata and the channel.
3. Execute opens the sealed refresh token (only for the approved revision), refreshes
   an access token, starts a resumable session with the approved metadata
   (`selfDeclaredMadeForKids: false`) and streams the R2 object to it.

| Point of failure | Recorded | Uploads |
| --- | --- | --- |
| Sealed secret does not open | `failed`, `credential-unreadable`, before `dispatching` | 0 |
| Token refresh refused | `failed`, `provider-rejected: credential-refused` | 0 |
| Media missing or size changed | `failed`, `provider-rejected: media-unavailable` | 0 |
| Session start refused or failed (no video exists yet) | `failed`, `provider-rejected: upload-refused` | 0 |
| Upload error, lost response or unexpected body | `unknown`, never re-uploaded | 1 |
| 200/201 with a video ID | `completed`, receipt is the video ID | 1 |

Uploading media is HTTP only; there is no MCP tool for binary bodies.

### Refusals name the field (2026-11-09)

Until this change every plan outside YouTube's limits was refused with one reason,
`must be JSON {title, description, privacyStatus, mediaId, bytes, sha256} within
YouTube limits`, so an app could not tell which field to fix. Propose now returns
`400 invalid`, field `content`, with the first broken rule as the reason:
`title must be a non-blank string`, `title must be at most 100 characters`, `title
must not contain < or >`, `description must be a string`, `description must be at most
5000 bytes`, `description must not contain < or >`, `privacyStatus must be one of
"private", "unlisted", "public"`, or `must name your uploaded video with its exact bytes
and sha256` (also used, as before, when the media is not the agent's stored video).
Content that is not JSON or not exactly those six keys keeps the shape reason without
the suffix. The rules themselves and what execute accepts are unchanged, so plans
proposed before still execute. `test/youtube-upload.test.ts` checks each reason and
that exactly 100 characters of title and 5000 bytes of description are accepted;
moving either limit by one, accepting a blank title or dropping the description's `<`
`>` check each fails it.

### Media reference refusals (2026-11-09)

The section above used `must name your uploaded video with its exact bytes and sha256`
both for a malformed media reference and for a well-formed one that is not stored, so
an app that answers it by uploading again (Envoylet does) also did so for a plan it had
built wrongly. A malformed reference is now named by field: `mediaId must be the ID that
PUT /v1/external-media returned` (not a lowercase UUID string), `bytes must be a
positive whole number` and `sha256 must be 64 lowercase hex characters`, checked in that
order after `privacyStatus`. `must name your uploaded video with its exact bytes and
sha256` is kept unchanged for a well-formed reference to media this agent has not
stored with that size and SHA-256 (unknown, another agent's, or a different size or
hash). A stored image named exactly is refused with `mediaId must name a video, not an
image`, since uploading it again cannot help. The plans accepted and what execute does
are unchanged. `test/youtube-upload.test.ts` checks every reason; mutation checks are
listed in the PR.

## Configuration for a real deployment (not done here)

- Google Cloud project with the YouTube Data API v3 enabled; OAuth consent screen
  with the two scopes; a Web OAuth client whose redirect URI is
  `<OAUTH_ISSUER>/connect/youtube/callback`.
- An R2 bucket bound to the mailbox worker as `EXTERNAL_MEDIA`.
- Auth worker: `GOOGLE_CLIENT_ID`. Mailbox worker secrets: `GOOGLE_CLIENT_ID`,
  `GOOGLE_CLIENT_SECRET`, `EXTERNAL_CREDENTIAL_KEY` (32 random bytes, base64url).
- Until Google verifies the project, the consent screen warns users, and videos
  uploaded through an unaudited API project are restricted to private.
- The three new tables and the `secret` column need the reviewed additive schema flow.

## Verification

- mailbox-worker `test/external-credentials.test.ts` (Google stubbed at the fixed
  endpoints): stored channel and sealed token; the secret does not open under another
  revision or agent; reconnect changes the revision; refusals for stale sign-in,
  another human, missing configuration, refused exchange, no refresh token, an
  unticked scope, no channel, a malformed channel, a redirect and malformed input,
  all storing nothing; a frozen owner's code is never exchanged; revoke erases the
  secret.
- auth-worker owner page and form tests, and three two-Worker tests with fake Google:
  consent stores the verified channel and the page shows it; a wrong state, another
  human's session and a cancelled consent store nothing; a stale session
  re-authenticates before leaving for Google. Removing the callback's human check
  fails the second test.
- mailbox-worker `test/youtube-upload.test.ts` (fake Google through the Worker's
  outbound service): media SHA-256 matches the stored bytes; an approved upload
  streams exactly those bytes once with the approved metadata and records the video
  ID; plans with a wrong hash or size, another agent's media or invalid metadata are
  refused; token refusal and a refused session start fail with nothing uploaded; a
  503, a lost response and a malformed video ID record `unknown` and never
  re-upload; a ciphertext sealed for another revision fails before Google is called.
  Dropping the hash check or treating an upload error as a rejection fails four
  tests.
- YouTube's own confirmation checks (2026-11-13, mutation scan): on connect, tokens on
  a refused exchange, an empty access or refresh token, a channel on a refused read,
  a channel answer not sent as JSON, over 64 KiB or not a JSON object, and a title
  that is not text are `provider-refused`; two channels are `no-channel`; a title
  over 200 characters is cut. On upload, tokens on a refused refresh, an empty or
  lost refresh are `credential-refused`; a session start on a status other than 200,
  with a session address off `www.googleapis.com/upload`, or lost is `upload-refused`
  with nothing uploaded; a video ID on a refused status records `unknown`; a video
  answered 201 completes; the upload never follows a redirect with the token and
  declares its exact `Content-Length`; a missing secret is `plan-unreadable` and a
  gone or resized stored file `media-unavailable`, both before any upload starts.
  Of 29 guard mutations scanned, 6 were already killed and 22 of the 23 survivors
  are now killed. One is equivalent and stays: refusing an empty refreshed access
  token inside the refresh (the caller refuses an empty token too).

## Limits

- One YouTube credential per agent; reconnecting replaces it.
- Media is never deleted yet and is limited to one request body (100 MiB).
- An `unknown` upload is not reconciled automatically; check the channel.
- Revoking does not revoke the token at Google; the owner can remove access in their
  Google account.
- The worker that decrypts the refresh token is the trusted execution path.
