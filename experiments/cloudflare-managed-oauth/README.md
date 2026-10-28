# Cloudflare Access Managed OAuth: approved isolated experiment

On 2026-10-27 the owner approved configuring an isolated Weave-only Access
Managed OAuth application/policies in the existing Cloudflare account.
This is not approval to change other applications, migrate D1, upgrade a
subscription, incur unconfirmed charges, or deploy the alternative custom
Auth Worker/KV/DO architecture. Weave remains responsible for OAuth protocol
validation and its own explicit per-account authorization, not a vendor SDK.

## First verify the existing execution path

`preflight.ts` only performs authenticated GETs against the fixed Cloudflare
API origin. It verifies the existing `weave-389` Workers account, then reads
Access application pagination and the Access organization. It does not read
secret values or print unrelated applications, policy rules or user details.
It never creates/modifies an application, policy, credential or database.
A successful GET does not prove that the token can write Access policies.

The workflow is tested on PRs without cloud credentials. The live read uses
only reviewed main after its test job succeeds, within the same existing
`production` environment as creation. Both workflows use the existing
`CLOUDFLARE_API_TOKEN` for account verification and Access operations. No second
token secret is required or selected, even when a stale trial token exists.
Environment protection still applies; PR verification receives no credentials.
Store credentials only in GitHub Actions secrets, never comments. Blocked API
statuses remain failures, not successful setup. Reusing a token does not grant
it new permissions: use the actual API result from this environment rather than
assuming an older repository-only check tested the same credential.

## Trial boundary

Do not intercept the current `/mcp` endpoint or homepage. Select an isolated
trial destination only after checking existing Access configuration. Do not
invent an allowed user email from a Git author or grant access to Everyone.
A specific allowed identity must be confirmed before allowing real login.
Cloudflare edge login alone is not Weave mailbox authorization: any later
adapter must verify signed assertions and issuer/audience/expiry, then resolve
an explicitly authorized Weave owner without sharing account controls.

References:
- https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/managed-oauth/
- https://developers.cloudflare.com/workers/configuration/routing/workers-dev/

This preflight is not a completed Managed OAuth login, cloud configuration,
or two-Agent public message/ACK/restart acceptance.


## One-time Access-only application creation (source ready, not yet provisioned)

The separately reviewed `provision-weave-access.yml` manual workflow supplies
an **explicit** creation operation, not an unattended recurring deployment.
It runs test/typecheck first, and the creation job runs only for an explicit
`create_trial=true` dispatch on protected `main`, within the existing
`production` environment. The caller must configure the following GitHub
Actions secrets in that environment or repository:

- `CLOUDFLARE_ACCOUNT_ID` and the existing `CLOUDFLARE_API_TOKEN` for the
  fixed `weave-389` account identity check, Access reads and the approved create.
  That same token must have account-scoped **Access: Apps and Policies Write**
  and access to the organization read endpoint. Do not create, copy, rotate or
  broaden a token merely because a differently named secret is absent;
- `WEAVE_ACCESS_TRIAL_EMAILS`: **two or more explicitly approved**, distinct
  exact email addresses, comma-separated. Do not use a wildcard, domain rule,
  Git author email, or Everyone. These addresses are read as a secret and are
  never printed by the tool.

After successful read-only Cloudflare account/application/organization
preflight, the command creates **one new** self-hosted Access application with
only `demo.weave-389.workers.dev/access-trial/*` as its public destination.
It includes exactly one inline Allow policy covering only the supplied emails,
never an implicit Everyone or bypass. Managed OAuth is enabled with DCR for
localhost and 127.0.0.1 clients, no arbitrary remote redirect URI, 15-minute
OAuth access tokens and a 24-hour grant. It does **not** change the public
`/mcp`, homepage, another Access app or a Worker binding, and performs no
D1 write/migration, secret installation, billing or plan operation.

The create API call is made **once**, without retry. Afterward the tool reads
back the new application's exact path/OAuth configuration and the attached
policy. A failed check means the application **may already exist**; inspect
Cloudflare and run the existing read-only preflight before attempting any
further operation. The command deliberately refuses to modify or reuse any
matching existing app. An API HTTP 403 proves only missing permissions, not
absence of an Access organization.

**Remaining acceptance:** even a successfully created Access app is not a
working private MCP connector. The opt-in origin route must be configured and
validated separately. Its current owner-subject binding requires optional D1
storage **not yet approved to migrate**. Browser consent with independently
allowlisted real users, per-user Weave account linking and two genuine Agent
client message/ACK/restart tests are still independent steps. Do not use an
Access edge-authenticated identity as Weave mailbox authority by itself.

Cloudflare specifications:
https://developers.cloudflare.com/api/resources/zero_trust/subresources/access/subresources/applications/methods/create/
https://developers.cloudflare.com/cloudflare-one/access-controls/policies/app-paths/
https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/managed-oauth/

### Safe Cloudflare 403 diagnostics

When Cloudflare rejects an Access GET, the reviewed-main preflight preserves
only up to four **numeric** provider `errors[].code` values (positive integers
at most 999999). Error bodies are read with an 8 KiB cap, then discarded; names,
messages, account IDs, token values, policy rules and arbitrary fields never
appear in logs. A malformed, non-JSON or oversized error yields an empty code
array and remains a failing HTTP response. No retry, redirect or write occurs.
A numeric error is diagnostic evidence, **not** proof of missing permissions or
permission to create the trial. Interpret it against current Cloudflare API
documentation before any environment or credential change.
