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
is immediate on the next private MCP call because the resource performs a
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

## Storage review gate

New additive Drizzle target: `oauth_identity_links` with primary key
`(issuer, resource, subject_digest)`, account FK (ON DELETE CASCADE), created,
updated and revocation ticks, plus owner index and digest format CHECK.
This feature is disabled by default. **The repository generates DDL from
the Drizzle target; a future deployment could plan creation automatically.**
Before enabling or deploying this branch: approve the exact generated DDL,
new D1 storage, retention, privacy, consent UX, data conversion and rollback.
Do not run `createMissingSchema`, a Cloudflare D1 write, or deploy this
branch to an existing D1 merely because local/hosted tests initialize a
separate empty test DB. No migration has been applied to the public demo.

## Test boundary

The branch's dedicated hosted Worker test exercises an actual Miniflare
workerd application and real D1, authorized account controls, link listings,
owner isolation, revocation, fail-closed missing tokens, and RFC 7662 validation
with a synthetic fixed AS response. This is not a real commercial provider,
human login or public Internet two-Agent proof. Private operational scopes
remain checked by `remote-private.ts` on every invocation.
