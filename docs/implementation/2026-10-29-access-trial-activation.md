# Access trial activation and approved identity-link storage — 2026-10-29

This record covers the one-time release path that turns on the Cloudflare
Access Managed OAuth trial for issue #236. It is source and synthetic/local D1
verification. It is **not** evidence that the release has run, or that two
real agents have exchanged messages.

## Owner approvals

- The Access application was created by `provision-weave-access.yml` with an
  owner-selected allow policy of two exact email identities (stored only as the
  `production` environment secret `WEAVE_ACCESS_TRIAL_EMAILS`; not repeated
  here). It protects only `demo.weave-389.workers.dev/access-trial/*`.
- On 2026-10-29 the owner approved creating the additive `oauth_identity_links`
  table (and its owner index) on the live demo D1, and enabling the trial
  routes, for this trial.

## What the release does

`deploy-weave-worker.yml` gains an optional `access_trial_application` input,
passed only to the protected `production` job as
`WEAVE_APPROVED_ACCESS_TRIAL_APPLICATION`. When it is a UUID, `deploy.ts`:

1. Refuses unless the release is `maintain` mode, has no first-party OAuth
   bootstrap or existing `AUTH_SERVER` binding, and the Worker has no
   `ACCESS_TRIAL_*` settings and no enabled `OAUTH_IDENTITY_LINKS_ENABLED`.
2. Reads that exact Access application. It must be named
   `Weave Managed OAuth Trial`, be `self_hosted`, have domain
   `demo.weave-389.workers.dev/access-trial/*` and Managed OAuth enabled. Its
   audience tag (`aud`) is read from the API, never typed by hand.
3. Reads the Zero Trust organization; its team domain must be
   `<team>.cloudflareaccess.com`.
4. Stages, in the maintenance, recovery and active uploads:
   `ACCESS_TRIAL_ENABLED=true`, `ACCESS_TRIAL_ISSUER=https://<team domain>`,
   `ACCESS_TRIAL_RESOURCE=https://demo.weave-389.workers.dev/access-trial/mcp`,
   `ACCESS_TRIAL_AUDIENCE=<aud>` and `OAUTH_IDENTITY_LINKS_ENABLED=true`.
5. Plans and applies the schema with the identity-link table **included**,
   during maintenance, then verifies that no statement is left unapplied.
6. After the active deployment, reads the Access-served
   `/.well-known/cloudflare-access-protected-resource/access-trial/mcp` and
   requires the same resource, `protected: true` and the same team as the only
   authorization server.

Any preflight mismatch stops before the maintenance upload. Ordinary releases
still omit the table. Later ordinary releases keep the trial settings through
`--keep-vars`, and the existing preflight already refuses to deploy with the
link flag enabled while the table is missing.

## Storage, privacy and retention

- DDL is the Drizzle target in `src/schema.ts` (`oauth_identity_links`):
  primary key `(issuer, resource, subject_digest)`, `account_id` foreign key
  to `accounts` with `ON DELETE CASCADE`, created/updated/revoked ticks, an
  owner index and a 64-hex digest `CHECK`.
- Stored per link: the issuer (team URL), the resource URL, a SHA-256 digest of
  `[issuer, resource, subject]`, the Weave account ID and timestamps. The
  Access subject is Cloudflare's opaque user ID; it is hashed, not stored.
  **No email address, Access JWT, OAuth bearer or Weave account control is
  stored.**
- A link exists only after the owner calls `POST /access-trial/link` with both
  a valid Access session and their Weave account control, plus
  `{"consent": true}`. Owners list and revoke their links with
  `/v1/accounts/oauth/links` and `/v1/accounts/oauth/links/revoke`.
- Retention: revoked links are retained so a digest cannot be silently claimed
  by another account; deleting the Weave account deletes its links.

## Rollback

- Disable the trial without touching D1: set `ACCESS_TRIAL_ENABLED` to
  anything other than `true` (the route then fails closed with 503), or delete
  the Access application, which stops all logins at the edge.
- Restore the previous Worker code with the recovery version that the release
  reports.
- The table is additive. It is not dropped automatically; dropping it would
  delete link history and needs its own approval.

## Verification

- `test/deploy.test.ts`: ordinary releases keep the table out of the plan;
  activation enables exactly the trial settings in every upload and creates the
  table; 13 fail-closed preflight cases stop before maintenance; a mismatched
  Access-served metadata response fails the release after activation; a real
  local D1 shows the approved policy creates the table and the ordinary policy
  does not. The three activation tests failed against the previous `deploy.ts`.
- `test/deploy-entry.test.ts`: only the protected production job receives the
  activation input.
- Not covered here: the live release, a real Access login, Access forwarding a
  signed assertion for a Managed OAuth bearer, and the two-agent exchange.
