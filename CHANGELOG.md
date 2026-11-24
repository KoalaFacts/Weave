# Changelog

Releases of the TypeScript Workers (`typescript/mailbox-worker` and `typescript/auth-worker`), which ship together to the
public demo through the production dispatch of `.github/workflows/deploy-demo.yml`. Versions follow SemVer; the
two Workers share one version. The `csharp/` implementation is in maintenance and is not versioned here.

## 0.1.0 — 2026-11-24

First versioned release. It covers everything merged after the last production deploy (`4fae8d3`, #531) up to and
including #567. No published HTTP or MCP contract is removed or changed incompatibly; every change below is additive.

### Schema (additive, each behind its own opt-in on the production dispatch)

Production must apply these tables in this release, or the deploy stops before any change (production already has
`owner_grants` and `external_action_keys` from the 2026-11-22 deploy):

- `app_webhooks` (#541) — `apply_app_webhooks_schema: true`
- `app_webhook_deliveries` (#550) — `apply_app_webhook_deliveries_schema: true`
- `owner_grant_approvals` (#555) — `apply_owner_grant_approvals_schema: true`
- `owner_grant_decisions` (#557) — `apply_owner_grant_decisions_schema: true`

### Added

- Push webhooks for owner apps: register, list and revoke an `https` callback over MCP (#548); a delivery is queued in
  the same transaction as every external-action status change (#551), sent signed with HMAC at least once with bounded
  retries (#552), and finished deliveries are pruned after 7 days (#553). `/account` lists each app's live callbacks
  (#554).
- App approval (A1): on the consent page an owner can let one app approve or reject their agents' pending plans,
  stoppable on `/account` (#556); the app decides on the exact plan digest with the owner page's revalidation (#558);
  the owner's action page and `/account`'s recent actions name the app that decided (#559, #562).
- `list_pending_actions` for an app that decides, across the owner's agents, paged with `after` (#563, #564).
- MCP parity with the HTTP API: `list_my_external_actions` (#533), `set_contact_block` (#537), `remove_contact` (#565),
  `revoke_my_address` (#566), `update_my_address` (#567).
- `/account` pages through older recent actions (#536).

### Fixed

- Publishing describes and refuses media plainly for text-only accounts (#532). The refusal reason strings for those
  accounts changed; apps that match the old text literally must update.
