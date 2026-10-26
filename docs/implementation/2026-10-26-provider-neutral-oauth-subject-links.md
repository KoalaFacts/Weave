# Owner-approved provider-neutral OAuth subject links — 2026-10-26

This is **source and isolated D1 verification, not approval for cloud migration,
a deployed identity provider, or public Agent acceptance**. Live schema changes,
identity migration, and Auth Server settings require explicit separate approval.

## User path and trust contract

An external RFC 7662-compliant authorization server issues a resource-bound,
short-lived opaque access token after the external user's own consent. The
operator chooses one allowlisted HTTPS issuer and its fixed introspection URL.
A user deliberately calls `POST /v1/accounts/oauth/links` with **both** their
Weave account control sent only to the Weave origin (header
`X-Weave-Account-Control`) and their external bearer issued for exactly
`OAUTH_RESOURCE` (header `Authorization`), with a JSON `{}` body. This
is a **first-party linking action**: the existing account control proves Weave
ownership; a freshly introspected token proving `iss`, `aud`, active status,
expiry and `weave:addresses.read` scope proves external subject control.
The external AS never receives the Weave account secret. A provider login by
itself never grants an Agent account. Do not create a link from email, a
supplied UUID, a request header, or model tool arguments.

The Worker stores only `SHA256("weave-oauth-subject-link-v1:" ||
JSON.stringify([issuer,resource,subject]))`, not the raw external subject or
bearer. Active links are exclusively keyed by (issuer,resource,digest) and
cannot be silently claimed by another account even after revocation. Owners
can view their own digests via `GET /v1/accounts/oauth/links` and revoke one
with `POST /v1/accounts/oauth/links/revoke` using the exact
`{issuer,resource,subjectDigest}`, authenticated by Weave control. Revocation

Owner link listings return at most 50 entries, including retained revoked
history, plus `truncated` and a `nextCursor` continuation when additional
entries exist. Fetch `GET /v1/accounts/oauth/links?cursor=<nextCursor>` until
`nextCursor` is null. The cursor identifies an ordered key, **not a bearer or
permission**; every page is independently restricted to the authenticated
Weave account. Invalid, duplicate, or unknown query parameters fail closed.
All retained links remain discoverable for owner revocation, including when
the first 50 records are revoked. No raw provider subject is disclosed.

Revocation is immediate on the next private MCP call because the resource performs a
fresh RFC 7662 introspection and D1 mapping read. A deleted account loses
its linked authorization; an AS outage or missing schema denies access.
No token, provider password, or control is stored in the link table.

The opt-in flag `OAUTH_IDENTITY_LINKS_ENABLED=true` makes **every** RFC
7662 token use explicit links, including UUID-shaped external subjects;
missing and revoked links cannot fall back to matching a UUID. Without
the flag, the historical operator-trusted Weave-native UUID subject behavior
is retained. The separate Cloudflare Workers OAuth Provider private binding
still verifies already-proven Weave UUID subjects and is not an external
issuer mapper. Managed Cloudflare Access opaque `oauth:` credentials and
its signed origin assertion require a separately validated edge adapter;
**this RFC 7662 path alone does not support Access Managed OAuth**.

## Separately hosted RFC 7662 introspection

External providers may publish an HTTPS introspection endpoint on an origin different from their trusted issuer. The resource defaults to requiring the issuer's origin. A deployment may explicitly pin `OAUTH_INTROSPECTION_TRUSTED_ORIGIN` to **exactly** the HTTPS origin of the configured `OAUTH_INTROSPECTION_URL` (including non-default port, no path, query, userinfo or fragment). A mismatched value, an isolated trust setting, or a mixture with `AUTH_SERVER` fails closed. The complete introspection URL remains operator-fixed: requests never follow redirects, token/Basic secrets are not sent to discovered or caller-supplied URLs, and the response must still match the separately configured `iss`, `aud`, scope, expiry and active status. This optional integration does not enable Cloudflare Access Managed OAuth.

## Storage review gate

New additive Drizzle target: `oauth_identity_links` with primary key
`(issuer, resource, subject_digest)`, account FK (ON DELETE CASCADE), created,
updated and revocation ticks, plus owner index and digest format CHECK.
This feature is disabled by default. The Drizzle target can generate its DDL,
but the normal demo release path now deliberately **defers** creation of
`oauth_identity_links` and its indexes in both `maintain` and
`completed-only` mode. Existing optional table constraints are still
validated; missing unrelated objects retain their existing checks. A currently
enabled OAuth link resolver with a missing table fails the deployment
preflight before maintenance, rather than silently provisioning a new store.

Before enabling OAuth identity links or applying the optional D1 DDL to the
live database, separately approve the exact generated DDL, storage/retention,
privacy, consent UX, conversion and rollback. A code-only release with this
feature disabled is not schema approval, and a synthetic local/hosted D1
migration is not a live deployment. No migration has been applied to the
public demo.

## Test boundary

The branch's dedicated hosted Worker test exercises an actual Miniflare
workerd application and real D1, authorized account controls, link listings,
owner isolation, revocation, fail-closed missing tokens, and RFC 7662 validation
with a synthetic fixed AS response. This is not a real commercial provider,
human login or public Internet two-Agent proof. Private operational scopes
remain checked by `remote-private.ts` on every invocation.
