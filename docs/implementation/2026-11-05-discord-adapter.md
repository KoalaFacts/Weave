# Discord publishing adapter

## Scope

Discord is a [publishing router](2026-11-05-publishing-router.md) adapter (`src/discord-provider.ts`, one entry in
`publishingAdapters`). The owner page renders its connect form from the adapter's single field.

An owner creates an incoming webhook in the channel's settings (Integrations, Webhooks) and pastes its URL. The URL
carries the webhook token, so the whole URL is the sealed secret. Connecting reads the webhook from Discord and
accepts only an incoming webhook (type 1) whose id matches the URL. The credential is `discord:<webhook id>`, with
the webhook's channel as its target account, shown as the webhook name and channel id.

The legacy `discordapp.com`, `canary.` and `ptb.` hosts and versioned paths are accepted in the URL. Every request
still goes to the fixed `https://discord.com/api/v10/webhooks/` API. A URL is never logged or returned.

No bot, OAuth application or Discord account permission is needed. Anyone holding the URL can post to that channel,
which the form's help text states.

## Post rules

- Text: up to 2000 characters, rendered as Discord markdown.
- Mentions never ping anyone. Every post is sent with `allowed_mentions: {parse: []}`, so `@everyone`, `@here`,
  roles and users stay plain text.
- Media: up to 10 files (JPEG, PNG, GIF, WebP, MP4), 10 MiB in all, which is Discord's limit for a server without
  boosts. Alt text becomes each attachment's description.
- No `publishAt` and no settings.

## Dispatch

- The webhook is read again before anything is sent. A server administrator can move a webhook to another channel,
  so a webhook that has moved since the plan was approved posts nothing (`webhook-moved`); the owner reconnects it
  and approves again. A deleted or unreadable webhook also posts nothing (`credential-refused`).
- Every file is read in full before anything is sent, so a missing file refuses with nothing posted.
- One `POST ...?wait=true` multipart request carries `payload_json` and the files. `wait=true` makes Discord return
  the created message.
- Outcomes:
  - 400, 401, 403, 404, 413 and 429 are definite refusals (`discord-refused-<status>`).
  - Any other status, a network failure, or a reply without a message id in the credential's channel is unknown and
    never sent again.
  - The receipt is `<channel id>/<message id>`.

## Verification

`test/discord.test.ts` runs against a Discord webhook fake. It covers:

- connecting stores the channel and the sealed URL;
- connecting refuses, without sending anything, a URL on another host, plain HTTP, a URL without its token, and a
  password;
- connecting refuses an unknown webhook, a channel-follower webhook and a mismatched id;
- the legacy host is accepted;
- the post rules;
- text is posted once, with mentions that never ping;
- files are uploaded as the exact stored bytes, with alt text;
- unsupported or unpinned media is refused at proposal;
- a webhook moved to another channel or deleted after approval fails with nothing posted;
- 400, 404 and 429 fail; 502, or a reply for another channel, is unknown, and neither is sent again.

Mutation checks: each of these breaks a test:

- dropping `allowed_mentions`;
- accepting non-incoming webhooks;
- accepting a reply for another channel;
- skipping the channel check before dispatch.

## Limits

- No scheduling, threads, forum posts, embeds, buttons or text-to-speech.
- A boosted server's larger upload limit is not used: posts stay within 10 MiB.
- Deleting the webhook in Discord makes the next post a definite refusal. The credential stays listed until it is
  revoked.
- A move between the pre-dispatch read and the post itself, a window of milliseconds, is not detected before
  sending. The reply's channel check then marks the outcome unknown.
