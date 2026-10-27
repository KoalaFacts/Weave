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
only reviewed main after its test job succeeds. `CLOUDFLARE_API_TOKEN` verifies
the existing account. An optional `CLOUDFLARE_ACCESS_TRIAL_TOKEN` is used for
Access reads; without it the existing token is tried without broadening its
permissions. Store credentials only in GitHub Actions secrets, never comments.
Blocked API statuses are retained as failures, not treated as successful setup.

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
