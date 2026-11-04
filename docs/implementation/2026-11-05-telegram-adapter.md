# Telegram publishing adapter

## Scope

Telegram is the first platform added only as a [publishing router](2026-11-05-publishing-router.md)
adapter (`src/telegram-provider.ts`, one entry in `publishingAdapters`). No RPC, route, form
kind or owner-page code changed: the owner page renders its connect form from the adapter's
fields.

An owner creates a bot with @BotFather and adds it to a channel or group as an administrator.
They then connect it with two fields:

- the bot token (secret, sealed like every other credential);
- the chat, as `@username` or a numeric ID.

Connecting asks Telegram:

- `getMe`: the token belongs to a bot;
- `getChat`: the chat is a channel, group or supergroup;
- `getChatMember`: the bot is its administrator or creator. In a channel, an administrator
  must also hold `can_post_messages`.

The credential is `telegram:<chat id>`, shown as the chat title and username.
Each chat is its own credential, and connecting a second chat with the same bot does not
revoke the first.

## Post rules

- Text: up to 4096 characters, or 1024 as the caption when media is attached. It is sent
  without `parse_mode`, so the approved text is posted literally.
- Media:
  - up to 10 files: JPEG or PNG photos up to 10 MB, MP4 videos up to 50 MB;
  - 50 MB per post in all, because the Bot API upload limit is 50 MB and files are read
    into memory before sending.
- No `publishAt` and no settings.

## Dispatch

- Every file is read in full before anything is sent, so a missing file refuses with
  nothing posted.
- The request depends on the media:
  - no media: `sendMessage`;
  - one file: `sendPhoto` or `sendVideo` with a caption;
  - several files: one `sendMediaGroup` album with the caption on the first item.
- Outcomes:
  - 400, 401, 403, 404, 413 and 429 are definite refusals (`telegram-refused-<status>`);
  - any other answer, a network failure or a reply missing its messages is unknown and is
    never sent again;
  - the receipt is `<chat id>/<first message id>`.
- The Bot API carries the token in the URL path. Request URLs are never logged or
  returned, and the endpoint is fixed, never configuration.

## Verification

`test/telegram.test.ts` runs against a Bot API fake:

- connecting stores the chat and a sealed token;
- connecting refuses a malformed token or chat (without calling Telegram), a wrong token,
  a user account, a private chat, a plain member, and a channel administrator without the
  posting right; a group administrator is accepted;
- the post rules;
- text is posted once as plain text;
- an album, and a single video, upload the exact stored files;
- unsupported or unpinned media is refused at proposal;
- 400, 403 and 429 fail, 502 is unknown, and neither is sent again.

Mutation checks:

- removing the channel posting-right check, treating an uncertain answer as a refusal, or
  narrowing the refusal statuses each fails a test.

## Limits

- No scheduling: Telegram's own scheduling is not available to bots.
- No formatting, buttons, silent posts or link previews. These would need settings, which
  the adapter does not take yet.
- Files go through memory, so 50 MB per post.
- Publication tracking is not needed: a sent message is already published.
