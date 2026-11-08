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
  required names, plus, for settings whose values the platform documents, the value's
  JSON type and closed list of values.

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

An adapter's text limit may depend on the account (Envoylet request
`postiz-channel-text-rules`, 2026-11-08): a Postiz channel takes the smaller of
Postiz's 7000 characters and its platform's limit, so text the platform would refuse
after approval is refused at proposal instead. X channels count 280 weighted
characters with the native X adapter's `xLength`; Bluesky 300 graphemes and 3000
bytes; `linkedin` and `linkedin-page` 3000 characters; `instagram` and
`instagram-standalone` 2200 characters. Threads keeps 7000: Meta counts an emoji as
its UTF-8 bytes, which no listed unit expresses, so Weave does not claim a Threads
limit. Limit: X Premium accounts, which X lets post longer text, are held to 280
through Postiz as through the native adapter: Weave does not read an account's
subscription tier. `textRules` and the description are computed per credential from the
same rule. The contract revision stays `postiz-post-v1`, since what is sent is unchanged:
a plan within the new limit dispatches as approved, and a longer plan approved before
the deploy is refused at execute (`plan-unreadable`) with no storage read or call.

Settings values (Envoylet request `postiz-setting-values`, 2026-11-08, first step):
each listed credential carries `settingRules: {<name>: {type, values}}` (null exactly
when `textRules` is; `{}` when no setting is checked beyond presence). `type` is
`string`, `boolean` or `number`; `values` is the closed list, or null when any value of
the type is accepted. A present setting of the wrong JSON type is refused with
"settings.<name> must be a <type>", and one outside the list with "settings.<name>
must be one of ...". The description names the allowed values and shows a boolean
setting as `true | false`. Listed so far, each from docs.postiz.com/public-api/providers/<channel>
and only for settings Postiz requires: X `who_can_reply_post` (`everyone`, `following`,
`mentionedUsers`, `subscribers`, `verified`); Instagram and Instagram Standalone
`post_type` (`post`, `story`); YouTube `title` (any string) and `type` (`public`,
`unlisted`, `private`); TikTok `privacy_level` (`PUBLIC_TO_EVERYONE`,
`MUTUAL_FOLLOW_FRIENDS`, `FOLLOWER_OF_CREATOR`, `SELF_ONLY`), `autoAddMusic` (`yes`, `no`),
`content_posting_method` (`DIRECT_POST`, `UPLOAD`) and the booleans `duet`, `stitch`,
`comment`, `brand_content_toggle` and `brand_organic_toggle`. Other settings (Pinterest
`board`, Reddit `subreddit`, Discord `channel`, and optional ones) are checked for presence
only. Not yet listed although Postiz documents them: X `post_type` (`post`, `article`;
article mode changes the text format and limit), `article_status` and the optional
booleans.
The contract revision stays `postiz-post-v1`: a plan approved before the deploy with a
value outside the list, which Postiz would refuse, is refused at execute
(`plan-unreadable`) before any env read or provider call.

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

Each value is trimmed before it is checked, verified with the platform and sealed (2026-11-08): a token pasted with a
space before or after it connects, and only the trimmed value is sent and stored; a value of only spaces is missing.
`connectTelegram_ValuesPastedWithSurroundingSpaces_ConnectsAndSealsTrimmedToken` covers it. Mutation checks, each
failing it: the values checked untrimmed; the untrimmed secret sealed.

The account name each platform sends back (a Telegram chat title, a Discord webhook
name, a Postiz channel name, a YouTube channel title) is stored as `targetDisplay` only
after `shownName` in `src/external-credentials.ts` passes it through `visibleExcerpt`
with the published 400-unit bound; a name with no visible character left (only
whitespace or default-ignorable characters such as U+200C or a variation selector)
becomes the provider-verified account id (2026-11-07). The owner overview, the approval page, the
credential list and pending actions (joined to the live credential row) read that stored value.

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
  Telegram (50 MB total, `maxAlt` 0), X (`maxAlt` 1000, from #372), and null for the unavailable YouTube
  credential. `publishing`
  `altRules_AdaptersThatSendNoAlt_RefuseAltBeforeApproval`: Telegram and Postiz refuse
  alt at propose with "alt is not supported here; omit it" and say so in their
  description (a reported finding: both accepted alt and then sent none). Their
  contract revisions stay `telegram-post-v1` / `postiz-post-v1`: what they send is
  unchanged, so approved plans without alt still dispatch, and a plan with alt
  approved before the deploy is refused at execute (`plan-unreadable`) without a
  provider call (`execute_AltApprovedBeforeAltWasRefused_RejectedWithoutSending`).
  `postizTextRules_ChannelWithAStricterPlatform_RefusedAtProposeAndListed`: each
  Postiz channel type accepts text at its limit, refuses one unit over (X with CJK
  weighting, Bluesky by graphemes and by bytes), and lists and describes the same
  limit; Threads and Mastodon keep 7000.
  `execute_PostizTextApprovedBeforeThePlatformLimit_RejectedWithoutSending`: an X
  channel plan over 280 is refused at execute before any read or send.
  `postizSettingRules_XReplySetting_OnlyDocumentedValuesAccepted`: the five X reply
  values are accepted; other strings (case, blank, padded) and non-strings are refused;
  Pinterest `board` stays presence-only and `constructor/...` lists `{}`.
  `postizSettingRules_InstagramYoutubeTiktok_DocumentedTypesAndValuesChecked`: the
  documented values and JSON types pass; an undocumented value, a string where TikTok
  takes a boolean (`"false"`), `0`, and a non-string title are refused; the listing and
  the `true | false` description match. Mutations killed (step 2): dropping TikTok
  `duet`; adding a value to `privacy_level`; dropping Instagram Standalone; dropping the
  boolean description.
  `execute_PostizSettingApprovedBeforeValuesWereChecked_RejectedWithoutSending`: an
  approved X plan with `who_can_reply_post: "anyone"` is refused before any read or send.
  `agent-credentials-media` lists `settingRules` per credential (TikTok's toggles and lists, X the
  reply list, null for the unavailable YouTube credential) and the description names
  the values. Mutations killed: dropping the type check; dropping the values check.
  Equivalent (survived, harmless): `Object.hasOwn` → `in` on the channel table and on
  the posted settings, since inherited names carry no entries.
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
- Stored account names: `telegram`
  `connectTelegram_TitleWithControlOrBidiCharacters_StoredDisplayVisibleAndWellFormed`
  `connectTelegram_TitleOnlyInvisibleCharacters_DisplayFallsBackToChatId` and
  `connectTelegram_TitleOnlyDefaultIgnorableCharacters_DisplayFallsBackToChatId`, and
  `external-credentials`
  `connectExternalCredential_YoutubeTitleWithBidiOverride_StoredDisplayVisible`, failed
  before the change (a newline, U+202E and U+200B were stored as sent). Mutations, each
  killed: the fallback removed; the cleaning removed from `connectAccount`; the
  cleaning removed from the YouTube connect; the fallback tested on an empty string
  instead of on a visible character (2026-11-07). Raising the 400-unit bound survives:
  every current adapter cuts its name shorter, so the bound only guards future adapters.

## Limits

- Settings are checked for presence and fixed values, not for each platform's value
  rules; the platform still refuses invalid values at dispatch.
- Account names stored before this change is deployed keep their text until the owner reconnects
  that account; HTML pages escape them either way.
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

### X alt text (2026-11-06, sub-step 2/2)

This supersedes the "No alt text" router change, the "Alt text: not taken" post rule and the alt text item in the X
limits above.

- **Rule:** each image may carry alt text of at most 1000 characters (`maxAlt: 1000`, the `maxLength` of X's
  `POST /2/media/metadata`, read on 2026-11-06). Longer alt text is refused before approval with "alt must be at most
  1000 characters here". The router measures UTF-16 code units, which are never fewer than the code points a JSON
  Schema `maxLength` counts. This rests on the published schema; X's own counting of alt text is not live-verified.
- **Dispatch:** after an image with alt text is uploaded, one signed `POST https://api.x.com/2/media/metadata` sends
  `{id, metadata: {alt_text: {text}}}` with the approved text unchanged. Only a 200 whose `data.id` is that media id
  counts as set. Any other answer or a network failure is a refusal, `media-alt-refused`, with nothing posted, because
  no post exists until `POST /2/tweets`. A post is never sent without the alt text the owner approved. Images without
  alt text, or with an empty alt text (which describes nothing), make no metadata call.
- **Verification** (`test/x.test.ts`):
  - `executeXPost_ImagesWithAlt_AltSetOnTheUploadedMediaBeforePosting` checks a 1000-character alt text with leading
    spaces and a newline. It is sent exactly, signed, on the right media id, only for the image that has it, and before
    the post.
  - `executeXPost_AltRefusedOrUnanswered_FailedWithNothingPosted` covers a 400, a 503, another media id, a 202 and a
    network failure.
  - `executeXPost_SecondImageAltRefused_FailedWithNothingPosted`: the first image's alt text is set, the second is
    refused, and nothing is posted.
  - `executeXPost_EmptyAlt_NoMetadataCall`: an empty alt text makes no metadata call (it failed before the fix, which
    sent `""`).
  - `xRules_Limits_TextImagesAltNoSchedule` refuses 1001 characters, and agents are no longer told X takes no alt.
  - Before the change, the new tests failed because alt was refused at proposal (`400`).
- **Mutation checks** (each fails a test): any id accepted as confirmation; a metadata failure ignored; no metadata
  call; any 2xx accepted; the limit raised to the router's 2000; the text trimmed before sending.
- **Limit:** the fake follows X's documented shape. No live X call has been made. X documents only a generic error
  response for this call (read on 2026-11-06), so every non-200 answer is treated as a refusal.

## Native LinkedIn adapter (2026-11-06)

### LinkedIn scope

LinkedIn is a publishing router adapter (`typescript/mailbox-worker/src/linkedin-provider.ts`, one entry in
`publishingAdapters`). It is shared plan step 4's native LinkedIn adapter on
[issue #365](https://github.com/KoalaFacts/Weave/issues/365). It posts on the owner's own member feed; company pages
are not offered.

The owner creates their own app in the LinkedIn developer portal with the "Sign In with LinkedIn using OpenID Connect"
and "Share on LinkedIn" products, generates a member access token with the `openid`, `profile` and `w_member_social`
scopes, and pastes it. It is the one secret field and is sealed as its value. Weave registers no LinkedIn app, keeps
no client secret and runs no OAuth redirect, so connecting needs no deployment configuration. A value not shaped like
a token is refused without being sent anywhere.

Connecting calls `GET https://api.linkedin.com/v2/userinfo` and accepts only an answer whose `sub` is a plain member
id. The credential is `linkedin:<sub>`, its target account `urn:li:person:<sub>`, shown by the member's name.

### LinkedIn post rules

- **Text:** up to 3000 characters as sent. LinkedIn reads commentary as its "little text" format, where
  `| { } @ [ ] ( ) < > # \ * _ ~` are markup. Every one of them is escaped with a backslash, so approved text appears
  exactly as written and never turns into a mention or hashtag the owner did not see; each therefore counts 2
  (unit `weighted characters`). The 3000 limit is not stated on the Posts API page; if LinkedIn counts differently it
  refuses the post (422) with nothing posted.
- **Media:** up to 9 images (JPEG, PNG or GIF), each at most 8,000,000 bytes. Files are read into memory one at a time.
  Alt text is taken; the post format's own limit (2000) is below LinkedIn's 4086. An empty alt text is not sent.
- **Not taken:** `publishAt`, settings, visibility other than public, documents, video and polls.

### LinkedIn dispatch

- Every call to `api.linkedin.com/rest/` names `LinkedIn-Version: 202610` (`linkedinVersion`) and
  `X-Restli-Protocol-Version: 2.0.0`, with the member token as a Bearer token.
- Each image: `POST /rest/images?action=initializeUpload` with the member as owner, then a `PUT` of the exact stored
  bytes to the returned `uploadUrl` with the token, expecting 200 or 201. The token is sent only to an `https://`
  address on `linkedin.com` or one of its subdomains, on the default port; any other upload address, an answer that
  is not an `urn:li:image:` id, or a refused upload is `media-upload-refused` with nothing posted. Image status is not
  polled: LinkedIn does not let a `w_member_social` token read `GET /rest/images`, so an image still processing makes
  LinkedIn refuse the post, which is a rejection.
- Then one `POST /rest/posts` with the member as author, `visibility: PUBLIC`, main-feed distribution and
  `lifecycleState: PUBLISHED`; one image as `content.media`, two or more as `content.multiImage`.
- **Outcomes:** 400, 401, 403, 422 (including a duplicate post), 426 (a sunset API version) and 429 are definite refusals
  (`linkedin-refused-<status>`). Anything else, a network failure, or a 201 whose `x-restli-id` is not an
  `urn:li:share:` or `urn:li:ugcPost:` id is unknown and never sent again. The receipt is that id.

### LinkedIn verification

`typescript/mailbox-worker/test/linkedin.test.ts` (10 tests) runs against a LinkedIn fake that answers 401 without the
member's Bearer token and 426 without the version headers. It covers connecting and sealing, refused and malformed
tokens with nothing stored or sent, escaping and counting, the post rules, a text post sent once, images uploaded
byte-for-byte and attached with alt text, one image as `media`, every upload failure with nothing posted and the token
never sent off LinkedIn's HTTPS hosts (redirects and dropped connections included), and each post outcome, including a
redirect and a dropped connection after the post call, which stay unknown and are not sent again. Mutation checks, each failing at least one test: no
escaping; plain length instead of the weighted count; accepting any upload host; accepting plain HTTP; not refusing
422 or 426; sending empty alt text; dropping the version header; accepting any `x-restli-id`; accepting any image id; always
using `multiImage`.

### LinkedIn limits

- Checked against LinkedIn's Posts, Images, MultiImage and little-text pages on 2026-11-06; not run against LinkedIn.
- LinkedIn sunsets each API version about a year after release, so `linkedinVersion` must be moved forward before
  then; once sunset, LinkedIn answers 426 and every post is refused with nothing posted.
- Member tokens expire after about 60 days and are not refreshed; a refused post is the sign to connect again.
- A token longer than 500 characters is refused at connect: the router takes field values of at most 512 printable
  ASCII characters. LinkedIn documents its tokens as about 500 characters but may lengthen them.
- Needs an owner deploy (no schema change) once merged.
