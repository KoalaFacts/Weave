# YouTube credentials: owner-connected, provider-verified, sealed

## Scope

Builds on [governed external actions](2026-11-04-worker-external-actions.md). An owner
connects YouTube for one of their agents from `/account`; the resulting credential is
what a later YouTube action is approved for. This increment stores and shows the
credential only: there is no YouTube operation yet (upload is the next increment), so
no YouTube action can be proposed. No real Google project was used and nothing was
deployed.

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

## Configuration for a real deployment (not done here)

- Google Cloud project with the YouTube Data API v3 enabled; OAuth consent screen
  with the two scopes; a Web OAuth client whose redirect URI is
  `<OAUTH_ISSUER>/connect/youtube/callback`.
- Auth worker: `GOOGLE_CLIENT_ID`. Mailbox worker secrets: `GOOGLE_CLIENT_ID`,
  `GOOGLE_CLIENT_SECRET`, `EXTERNAL_CREDENTIAL_KEY` (32 random bytes, base64url).
- Until Google verifies the project, the consent screen warns users, and videos
  uploaded through an unaudited API project are restricted to private.
- The two new tables and the `secret` column need the reviewed additive schema flow.

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

## Limits

- One YouTube credential per agent; reconnecting replaces it.
- Revoking does not revoke the token at Google; the owner can remove access in their
  Google account.
- The worker that decrypts the refresh token is the trusted execution path.
