# Postiz publishing: text, images and video through owner-approved Postiz channels

## Scope

Builds on [governed external actions](2026-11-04-worker-external-actions.md) and the
vault and media store from [YouTube](2026-11-04-youtube-credentials.md). Postiz Cloud
is a provider behind the same approval, one-time dispatch and unknown-outcome rules,
so agents can publish to every channel the owner connected in Postiz while native
adapters are built one platform at a time. Nothing here copies Postiz code (AGPL);
it uses Postiz's documented public API only. No real Postiz account was used and
nothing was deployed.

## Connect

On `/account`, **Connect Postiz** (per agent; the [publishing router](2026-11-05-publishing-router.md)'s generic
connect form, posted to `/account/connect`) takes the owner's Postiz API key in a
password field and needs a GitHub sign-in in the last 10 minutes; a stale session is
sent to GitHub and the key is dropped. The mailbox lists the key's integrations and
creates one credential per enabled, supported channel: reference
`postiz:<integration id>`, target account `<identifier>/<integration id>`, display
`<identifier> · <name> (@profile)`, the key sealed to each row, a fresh revision every
time. Postiz channels of the agent that the key no longer lists are revoked.

Supported channel types: x, linkedin, linkedin-page, facebook, instagram,
instagram-standalone, threads, bluesky, mastodon, telegram, youtube, pinterest, reddit,
discord, tiktok.

## publish_post

Content is JSON `{text, media: [{mediaId, bytes, sha256}], settings, publishAt?}`:

- `text` (≤ 7000 characters, or the channel platform's smaller limit: X 280 weighted
  characters, Bluesky 300 graphemes and 3000 bytes, LinkedIn 3000 characters,
  Instagram 2200 characters; see the [publishing router](2026-11-05-publishing-router.md))
  or at least one media item; at most 10 media.
- `settings.__type` must equal the channel's type and include the fields Postiz
  requires for it (for example YouTube `title` and `type`; TikTok
  `privacy_level`, `duet`, `stitch`, `comment`, `autoAddMusic`,
  `brand_content_toggle`, `brand_organic_toggle`, `content_posting_method`;
  Instagram `post_type`; X `who_can_reply_post`). X `who_can_reply_post` must be one of
  `everyone`, `following`, `mentionedUsers`, `subscribers` or `verified`, and is listed
  in the credential's `settingRules`; other settings are checked for presence only.
- Every media item is pinned to the agent's own uploaded file with its exact size and
  SHA-256, and must be JPEG, PNG, GIF, WebP or MP4 (what Postiz accepts).
- `publishAt`, when present, is a future UTC instant within 90 days.

The owner sees the full JSON and a preview of each file (served only to that owner,
only for files the action names) before approving.

## Dispatch

1. For each file, Weave mints a 15-minute signed URL
   (`/v1/external-media/{mediaId}/content`, HMAC over agent, media and expiry) and
   calls Postiz `upload-from-url`. Media in Postiz's library publishes nothing, so any
   failure here is `failed`, `media-upload-refused`.
2. `POST /posts` with `type` `now` or `schedule`, the approved text, media and settings,
   to the approved integration only.

| Outcome | Recorded |
| --- | --- |
| Approved schedule already passed | `failed`, `schedule-passed`, nothing sent |
| Postiz 400, 401, 403, 422 or 429 on `/posts` | `failed`, `postiz-refused-<status>` |
| 200 with exactly one `postId` | `completed`, receipt `posted:<id>` or `scheduled:<id>` |
| Anything else (5xx, lost response, unexpected body) | `unknown`, never re-sent |

Postiz has no idempotency key, so an `unknown` post must be checked in Postiz
(`GET /posts`) before anything is proposed again. A `scheduled:` receipt means
Postiz accepted the schedule, not that the platform published it.

## Configuration for a real deployment (not done here)

- A Postiz Cloud subscription (owner's decision and cost); channels connected in Postiz.
- Mailbox worker: `EXTERNAL_CREDENTIAL_KEY` (32 random bytes, base64url), the R2
  binding `EXTERNAL_MEDIA`, and `EXTERNAL_MEDIA_ORIGIN` (its public https origin, which
  Postiz must be able to reach).
- The additive schema flow for the tables added in this stack.

## Platform notes (from docs.postiz.com, checked 2026-11-04)

- Auth header is the raw API key, no `Bearer`.
- Create-post rate limit is documented as 90–100 per hour in one place and 30 per hour
  in another; treat 30 as the safe figure. `upload-from-url` is also 30 per hour.
- `upload-from-url` rejects private addresses and needs a publicly reachable URL;
  Postiz warns against URLs that expire before it fetches them.
- TikTok `content_posting_method: UPLOAD` only sends to the creator's inbox; use
  `DIRECT_POST` to publish.
- Post-creation JSON bodies are limited to 50 MB; recommended video is MP4/H.264/AAC,
  ≤ 30 fps, ≤ 1920×1080 (portrait 1080×1920 for TikTok and Shorts).

## Publication tracking

Postiz queues every post, so a completed action only means Postiz accepted it. When a
`publish_post` completes, the provider returns `trackFrom` (two minutes after the post
is due) and the same transaction writes an `external_publications` row in `queued`.
The 5-minute cron then asks Postiz with `GET /posts?startDate&endDate`. Postiz has no
read by ID and allows 30 calls an hour, so one listing per agent covers all of that
agent's due posts, and at most 5 agents are asked per run.

- `PUBLISHED` becomes `published` with `releaseURL` when it is an https URL.
- `ERROR` becomes `failed`. Postiz's `error` is the platform's own text, so it is
  stripped of invisible and control characters and bounded to 300 characters.
- `QUEUE`, `DRAFT` and a post Postiz no longer lists stay `queued`. They are
  re-checked after 15 minutes, doubling up to 6 hours, and are no longer asked about
  7 days after they were due.
- A failed or unanswered listing only postpones the check; it never changes a state
  or counts as a check.
- A disconnected credential stops tracking without asking.
- Tracking only reads: it never dispatches or retries anything.

`get_external_action`, `execute_external_action` and the HTTP reads return
`publication {state, url, error, checkedAt}` or null. The match assumes the `postId`
that post creation returns is the listed post's `id`. That is documented by Postiz
but not yet verified against a real account.

## Verification

- mailbox-worker `test/postiz.test.ts` (fake Postiz through the Worker's outbound
  service, which fetches the signed URL back from the Worker): connect stores one
  sealed credential per enabled supported channel and reconnect revokes vanished ones
  and rotates revisions; refusals store nothing; plans with the wrong channel type,
  missing required settings, unpinned or unsupported media, blank text, a past
  schedule or extra fields are refused; text posts once to the approved channel;
  media reaches Postiz as the exact bytes and a tampered or re-targeted signature is
  404; a schedule is sent as `schedule`; Postiz refusals and media failures create no
  post; a 502, a lost response and a malformed body are `unknown` and never re-sent.
  Dropping the channel-type check, the signature check or the unknown handling fails
  five tests.
- auth-worker page and form tests, and three two-Worker tests: connecting with a key
  stores and lists the channels without rendering the key; a stale session and the
  key-less `/account` form connect nothing; a preview is served only to the owner and
  only for files the action names. Removing the "named by this action" check fails
  that test.

- Publication tracking (`test/postiz.test.ts`, the sweep run in this process against a
  fake Postiz listing, plus one run of the Worker's own cron):
  - an immediate post is tracked and becomes published with its URL, then is never
    asked about again;
  - a platform error becomes failed with a bounded, visible message;
  - queued and unlisted posts back off, and a failed listing postpones without
    counting;
  - tracking stops after the window and when the credential is disconnected;
  - two posts of one agent take one listing call.

  Mutation checks: removing the error cleanup, the cron call, the stop on a
  disconnected credential, or the not-asked rule each fail a test.

## Limits

- One Postiz key per agent; reconnecting replaces every Postiz credential of it.
- Media is limited to one Worker request body (100 MiB). It is deleted 7 days after its
  upload and its last use (see [retention](2026-11-04-agent-credentials-media-upload.md#retention)).
- No Postiz webhook: publication state is learned by polling, so it lags by up to the
  current backoff. The owner page does not show it yet.
- The Worker that opens the sealed key is the trusted execution path.
