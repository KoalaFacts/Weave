# Inspect token-management capability through GitHub Actions

The owner requested Actions rather than relying on a Cloudflare management
plugin. This narrowly scoped action uses the existing protected `production`
`CLOUDFLARE_API_TOKEN`; no second secret is needed.

`Inspect existing Weave token management (read-only)` runs on relevant reviewed
main pushes or manual workflow dispatch. PRs run credential-free Node24 tests
and strict TypeScript checking only. The live job checks the current main SHA,
verifies the `weave-389` Workers account, verifies the current user/account token,
and reads only that token's metadata and the permission-group catalogue.

Every HTTP request is a bounded, non-redirecting GET. Token IDs, names, policies,
IP restrictions, raw error bodies and secrets are not logged. HTTP 403 from
`token-details` is recorded separately from the existing Access app/organization
403s. No token list, other token, account or application is read or changed.

`managementWriteDeclared` reports only an allow-policy declaration. It is not
an effective-authority decision: resource restrictions, deny policies and
provider rules may still prevent writing. `writePermissionVerified` is always
false. A `readable` result proves metadata access only, not an Access application,
login or Agent communication. The existing Access preflight remains unchanged
and its independent failures are not suppressed.

This delivered version has **no mutation mode**: no PUT/POST/PATCH/DELETE, token
rotation, permission changes, D1 writes or app creation. The earlier local
proposal that included permission updates was not published after a platform
safety check blocked its submission. It is not an executable fallback.

After merging, inspect the real main job logs before deciding the next authorized
operation. A missing plugin is not the same as an unavailable API; conversely,
writing an Action cannot manufacture missing provider-side authorization.
Final least-privilege cleanup after real two-Agent acceptance is still required.

Official contracts:
- https://developers.cloudflare.com/api/resources/user/subresources/tokens/methods/verify/
- https://developers.cloudflare.com/api/resources/user/subresources/tokens/methods/get/
- https://developers.cloudflare.com/api/resources/accounts/subresources/tokens/methods/get/
- https://developers.cloudflare.com/api/resources/user/subresources/tokens/subresources/permission_groups/methods/list/

Verification: `node --test experiments/cloudflare-managed-oauth/*.test.ts` on
Node24 and the existing strict typecheck. Fake HTTP regressions are not live
Cloudflare permission evidence.
