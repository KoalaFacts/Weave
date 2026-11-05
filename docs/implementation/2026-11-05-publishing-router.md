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
- the media count, the accepted types each with its largest size, and an optional
  total;
- the scheduling horizon, or none;
- for each account, the settings it requires: fixed values (Postiz's `__type`) and
  required names.

Where the platform has no use for something, the router refuses it: a `publishAt` for
a platform that posts immediately only, or settings for an adapter that takes none.

The router derives the description `list_my_credentials` returns from the same rules,
so what agents read and what is enforced cannot drift apart. At dispatch the router
re-reads the files, and a file gone or changed since approval sends nothing. The
adapter receives the parsed post with each file's stored type.

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

## Limits

- Settings are checked for presence and fixed values, not for each platform's value
  rules; the platform still refuses invalid values at dispatch.
- One secret field per connector. A platform that needs an OAuth redirect (YouTube)
  keeps its own flow.
- A post goes to one credential. Sending the same post to several channels is one
  proposal per channel, each approved on its own.
