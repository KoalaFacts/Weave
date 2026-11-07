# Publishing router: one post format, one connect flow, platforms as adapters

## Scope

Weave is a router for posts. It knows no platform. An agent proposes one post in a
single format, the owner approves it, and the router hands it to the adapter of the
credential's provider. Each platform used to bring its own connect RPC, form route,
owner-page form and content format: Postiz, then Bluesky. Now an adapter only declares
three things:

- how an owner connects it;
- what its platform accepts;
- how to send one post.

Validation, the description agents read, media checks, approval, the
at-most-once dispatch record and the connect flow belong to the router (`src/publishing.ts`).

Preserved:

- the Postiz and Bluesky contract revisions, because every plan they accepted still
  parses and dispatches the same way;
- their dispatch steps and outcome rules;
- credential references (`postiz:<id>`, `bluesky:<did>`), sealing and revisions;
- YouTube, whose OAuth redirect and `upload_video` operation stay as they are.

Removed, with no compatibility shim (pre-1.0):

- the `connectPostiz` and `connectBluesky` RPCs;
- `/account/postiz` and `/account/bluesky`;
- the `connect-postiz` and `connect-bluesky` form kinds;
- the `postiz-connected` and `bluesky-connected` results, which become one
  `account-connected`.

## The post format

Every publishing adapter takes `{text, media: [{mediaId, bytes, sha256, alt?}], settings?, publishAt?}`,
Postiz's shape.

An adapter's `rules` declare:

- a text limit in characters or graphemes, with an optional byte limit and an
  optional lower limit when media is attached (a caption);
- the media count, the accepted types each with its largest size, an optional total,
  and an optional alt text limit, refused before approval so that alt text is never
  shortened at dispatch. An adapter whose request carries no alt text sets it to 0
  (X, Telegram, Postiz), so an owner never approves a description that is not sent;
- the scheduling horizon, or none;
- for each account, the settings it requires: fixed values (Postiz's `__type`) and
  required names.

Where the platform has no use for something, the router refuses it: a `publishAt` for
a platform that posts immediately only, or settings for an adapter that takes none.

The router derives the description `list_my_credentials` returns from the same rules,
so what agents read and what is enforced cannot drift apart. Since 2026-11-06
(Envoylet request `credential-text-rules`) each listed credential also carries
`textRules: {max, unit, maxWithMedia, maxBytes}` (null for a credential whose content
is not a post, or whose provider is unavailable): the same text limits as data, so an
app can count before proposing without parsing the description. `unit` is
`characters` (UTF-16 code units), `graphemes` or `weighted characters` (X); clients
should treat an unknown unit as no limit. Beside it, `mediaRules: {max, types,
maxTotalBytes, maxAlt}` (Envoylet request `credential-media-rules`) carries the media
limits from the same rules: the file count, each accepted type with its largest file
in bytes, the total-bytes cap or null, and `maxAlt` (0: the platform takes no alt;
null: only the post format's 2000-character limit). It is null exactly when
`textRules` is. The router still decides at proposal.

At dispatch the router re-reads the files, and a file gone or changed since approval
sends nothing. The adapter receives the parsed post with each file's stored type.

## Connecting

An adapter's `connector` declares:

- a label and help text;
- its fields (name, label, whether secret, an HTML pattern, a placeholder). Exactly
  one field is the secret to seal.
- `verify`, which asks the platform which accounts the values act as;
- `replaces`: whether one secret covers all of the agent's accounts on that platform.
  This is true for Postiz, whose key lists every channel, so channels it no longer
  lists are revoked.

The owner overview lists the connectors of the adapters this deployment can use. The
authorization server renders one form per connector and agent from those field lists:

- a secret field becomes a password input;
- labels, help and patterns are escaped;
- the form carries a signed `connect-account` token bound to the agent and provider.

`POST /account/connect` needs a sign-in in the last 10 minutes. It passes the typed
values to `connectAccount`, which accepts exactly the adapter's field names with
printable values up to 512 characters. The authorization server never stores, logs or
renders the values.

Adding a platform is now one adapter file plus one entry in `publishingAdapters`.
Neither the authorization server, the owner page nor the RPC surface changes.

## Verification

- mailbox-worker `test/publishing.test.ts`:
  - the post shape;
  - Postiz settings and schedule rules, and their description;
  - a test-only adapter is validated, gets the parsed post and stored files, and is
    not called when a file disappeared after approval.
- `textRules` on `GET /v1/external-credentials` and `list_my_credentials`:
  `agent-credentials-media` (Postiz, and null for an unavailable YouTube credential),
  `bluesky` (with its 3000-byte cap), `discord`, `telegram` (1024 with media) and `x`
  (weighted). `mediaRules` in the same tests: Postiz (five types, no total, `maxAlt`
  0), Bluesky (1 MB images), Discord (10 MiB per file and in all, alt 1024),
  Telegram (50 MB total, `maxAlt` 0), X (`maxAlt` 0), and null for the unavailable YouTube
  credential. `publishing`
  `altRules_AdaptersThatSendNoAlt_RefuseAltBeforeApproval`: Telegram and Postiz refuse
  alt at propose with "alt is not supported here; omit it" and say so in their
  description (a reported finding: both accepted alt and then sent none).
- `test/postiz.test.ts` and `test/bluesky.test.ts` run their connect, plan and
  dispatch tests through the router. Bluesky's limits are checked through the router,
  and so is the exact-field rule.
- auth-worker portable tests:
  - the generic form reader takes the token and then the fields, and refuses: empty,
    misplaced, duplicate or badly named fields; too many fields; another form's token;
    another human; an expired form;
  - pages are built from real adapter descriptors;
  - a hostile descriptor is escaped.
- workerd tests connect Postiz and Bluesky through `/account/connect` and refuse an
  extra field.
- Platform text that Weave stores (a Postiz `ERROR` message as `publication.error`, a
  test provider's refusal reason after `provider-rejected: `) goes through
  `visibleExcerpt` in `src/visible-text.ts`: invisible and control characters become
  spaces, whitespace collapses, lone surrogates become U+FFFD, and the cut never splits a
  surrogate pair. `postiz` `publication_PlatformErrorCutInsideSurrogatePair_ErrorStaysWellFormed`
  and `external-actions`
  `executeExternalAction_RejectionReasonWithControlsAndSplitPair_StoredVisibleAndWellFormed`
  pin it; dropping the pair check, the invisible-character replacement or the
  well-formed step each fails at least one of them (2026-11-06).

## Limits

- Settings are checked for presence and fixed values, not for each platform's value
  rules; the platform still refuses invalid values at dispatch.
- One secret field per connector. A platform that needs an OAuth redirect (YouTube)
  keeps its own flow.
- A post goes to one credential. Sending the same post to several channels is one
  proposal per channel, each approved on its own.

## Native X adapter (2026-11-06)

### X scope

X is a publishing router adapter (`typescript/mailbox-worker/src/x-provider.ts`,
one entry in `publishingAdapters`). It is shared plan step 4's native X adapter on
[issue #365](https://github.com/KoalaFacts/Weave/issues/365). Posting to X through a Postiz channel still works and
is unchanged.

The owner connects with OAuth 1.0a user context. In the X developer portal they give their own app Read and Write
permission, then paste four values: the app's API key and API key secret, and the access token and access token secret
the portal generates for their own account. Weave registers no X app, keeps no X client secret and runs no OAuth
redirect, so connecting needs no deployment configuration. Posts use the owner's app and its X API access.

Connecting signs `GET https://api.x.com/2/users/me`. It accepts the answer only when it names a numeric user id and a
username, and that id is the one the access token names before its dash. Keys for another account are refused rather
than relabelled. The credential is `x:<user id>`, with that id as its target account, shown as `@username`.

Values that are not shaped like X keys are refused without being sent anywhere. Most passwords fail that shape check;
a long alphanumeric one does not, and X then refuses it.

### X router changes

- **Several secret fields.** The router used to seal exactly one secret field. An adapter may now declare several. One
  secret field is still sealed as its value, so stored Postiz, Bluesky, Telegram and Discord credentials are unchanged.
  Several fields are sealed together as one JSON object of their values by field name. This supersedes the "one
  secret field per connector" limit and the "exactly one field is the secret to seal" rule above. The authorization
  server already rendered each secret field as a password input and passed every field on, so it does not change. Its connect form takes at most 4 fields
  (`maxConnectFields` in `typescript/auth-worker/src/owner-forms.ts`), and X uses all 4.
- **Weighted text.** A text rule may use the unit `weighted characters` with a `weigh` function. The adapter supplies
  the function; the router applies it before approval and names the unit in the description agents read.
- **No alt text.** `maxAlt: 0` refuses any alt text with "alt is not supported here; omit it". The description tells
  agents the platform takes none.

### X post rules

- **Text:** up to 280 weighted characters, counted as X does:
  - text is NFC-normalized first;
  - code points in X's light ranges (U+0000–U+10FF, U+2000–U+200D, U+2010–U+201F and U+2032–U+2037) count 1, and
    others 2;
  - a grapheme shaped like an emoji counts 2. That covers a flag, a keycap on `0`–`9`, `#` or `*`, a tag flag, and up
    to four pictographs joined by ZWJ, each with an optional variation selector and skin tone. Any other grapheme
    counts all of its code points, so joiners, tags or marks piled onto an emoji are counted;
  - a link with `http://` or `https://` and an ASCII domain counts 23 whatever its length. It ends at the first
    character that is not an ASCII URL character, so CJK text after it is counted;
  - a scheme starts a link only after a character that cannot be part of a word: `see:https://x.co` is a link,
    `seehttps://x.co` is counted at its own length;
  - a link whose host is not plain ASCII (`https://例え.jp`) still makes its word count at least 23 per scheme;
  - a bare domain such as `example.com` counts 23 or its own length, whichever is more, because X may or may not link
    it. A word with a non-ASCII letter before `.tld` (`例え.jp`) counts at least 23;
  - leading brackets and quotes, and trailing punctuation, are counted on their own.

  Every shortcut is taken only where X takes it, so the count may exceed X's but should not fall short of it. If it
  ever does, X refuses the post at dispatch with nothing posted. Counting is linear and stops once the text is over
  280, so a megabyte of text is refused in milliseconds.
- **Media:** up to 4 images (JPEG, PNG or WebP), each at most 5,000,000 bytes. GIF and video are not offered: X takes one
  GIF or video per post, never mixed with images, and video needs the chunked upload.
- **Alt text:** not taken. The X API v2 alt text call was not verified when this was written, so the router refuses alt
  text rather than dropping approved text silently.
- **Not taken:** `publishAt`, settings, replies, quotes, polls and threads.

### X dispatch

- Every request is signed with OAuth 1.0a HMAC-SHA1 over the method, the URL and the oauth parameters. JSON and
  multipart bodies are not signed, as OAuth 1.0a specifies.
- Each image is read in full and uploaded with `POST https://api.x.com/2/media/upload`, as multipart `media` with
  `media_category=tweet_image`. Any 2xx with a numeric `data.id` is accepted. Uploading publishes nothing, so a missing
  file, a refused upload, a network failure or an answer without a numeric media id is a refusal with nothing posted
  (`media-unavailable`, `media-upload-refused`).
- Then one `POST https://api.x.com/2/tweets` with `{text?, media?: {media_ids}}`. An empty text is omitted.
- **Outcomes:**
  - 400, 401, 403 and 429 are definite refusals (`x-refused-<status>`). X answers 403 to a duplicate post, for example.
  - Any other status, a network failure, or a reply other than 201 with a numeric post id is unknown and never sent
    again.
  - The receipt is the post id.

### X verification

`typescript/mailbox-worker/test/x.test.ts` runs against an X API fake. The fake checks every OAuth 1.0a signature with
its own HMAC-SHA1 and RFC 3986 encoding, sorts by name and then value, and answers 401 to an unsigned or wrongly
signed request. It covers:

- connecting stores the user and seals all four values, none of them in clear, and the owner overview offers four
  secret fields;
- connecting refuses, without sending anything, a value that is not an X key and an access token without its user;
- connecting refuses a missing field, a wrong signature, an answer without a user, another user than the token names,
  and a server error;
- the weighting:
  - CJK, emoji sequences, a variation selector, a skin tone, a flag, keycaps and a tag flag;
  - marks and joiners piled onto an emoji, too many pictographs in one sequence, and a keycap mark on a letter;
  - a long link, a link followed by CJK, a link inside CJK text, CJK brackets, a scheme after punctuation, a scheme
    joined to a word, a scheme without a domain, and links to international hosts;
  - a bare domain, an international domain and a number;
  - NFC and punctuation;
  - counting a megabyte of five adversarial shapes takes under a second each;
- the post rules: 280, CJK at 141, a link that tips the count over, 5 images, alt text, `publishAt`, settings and an
  empty post;
- text is signed and posted once;
- images are uploaded as the exact stored bytes, then attached by media id;
- a refused upload, a server error, an answer without a media id, a network failure and a second image refused after
  the first was uploaded each fail with no post created;
- 400, 401, 403 and 429 fail. 500, 503, a 201 without a post id, a 200 and a lost connection are unknown. None of them
  is sent again.

Mutation checks: each of these breaks a test:

- dropping the check that the token's user is the one X names;
- dropping `maxAlt: 0`;
- signing without the access token secret;
- treating 5xx as a refusal;
- posting after a refused upload;
- counting plain characters instead of weighted ones;
- counting links at their own length;
- not counting an international domain as a link;
- not counting at least 23 for a link to an international host;
- starting a link at a scheme joined to a word;
- treating any grapheme with a pictograph as one emoji, or allowing longer ZWJ sequences;
- dropping the early stop past 280;
- dropping flags from the emoji check;
- accepting any 2xx as confirmation;
- sealing only the first secret field.

A second check that the sealed token's user matches the credential before dispatch survived mutation, because the
vault already binds the secret to its credential row. It was removed rather than kept untested.

A local gate passes. No request is made to the real X API: tests use the fake, and no post is created.

### X limits

- The X API endpoints and shapes come from X's v2 documentation, read on 2026-11-06, and are tested against a fake. No
  live X post has been made. Demo acceptance needs an owner with an X developer app.
- X's pricing and rate limits apply to the owner's app. A 429 is a refusal, and the owner approves again later. A 402
  (no API credit) is treated as unknown, because X's documentation read here does not say that nothing was posted.
- X Premium accounts can post more than 280 characters. This adapter refuses those posts before approval.
- The tests call `connectAccount` directly. No workerd test connects X through the authorization server's
  `/account/connect` form.
- The sealed secret's unreadable-JSON path (`credential-refused`) is not tested. The vault binds the secret to its row,
  so it is not reachable without a corrupted row.
- No alt text, GIF, video, scheduling, replies, quotes, polls or threads.
- Revoking the app's tokens in X makes the next post a definite refusal (401). The credential stays listed until it is
  revoked in Weave.
- OAuth 2.0 user tokens are not supported. They expire within hours and need a Weave-registered X app and a stored
  refresh token, which rotates on use.
