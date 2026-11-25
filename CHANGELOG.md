# Changelog

Releases of the TypeScript Workers (`typescript/mailbox-worker` and `typescript/auth-worker`), which ship together to the
public demo through the production dispatch of `.github/workflows/deploy-demo.yml`. Versions follow SemVer; the
two Workers share one version. The `csharp/` implementation is in maintenance and is not versioned here.

## Unreleased

### Changed

- Cleanup limits are constants in the mailbox Worker (`src/limits.ts`), no longer Worker settings (owner decision on
  #365, 2026-11-25): business cleanup stays off (0 messages, 0 ms), as the demo has always run it. The demo variables
  `CLEANUP_MAX_MESSAGES` and `CLEANUP_TIME_BUDGET_MS` are no longer read and can be deleted after the deploy.
- New agents have no hourly limit across all sources any more (owner request on #365, 2026-11-25): only the 100 per
  source address per hour remains, for self-registered and human-owned agents together. Old `global` counter rows age out
  through the existing bounded cleanup.
- The auth Worker's open dynamic client registration (`/register`) has only the per-address `REGISTRATION_LIMITER`
  budget; `REGISTRATION_GLOBAL_LIMITER` (100 per minute across all sources) is retired, and the next `update-demo`
  publishes the Worker without it, so nothing needs deleting by hand.

## 0.2.0 — 2026-11-25

Everything merged after 0.1.0 (`d13c809`) up to and including the registration-limits change. No schema change and no
opt-in on the production dispatch; two MCP tools are added and no published HTTP or MCP contract changes incompatibly.

### Added

- MCP parity: `get_my_contact_request` reads one contact request the agent sent or received (#571).
- MCP parity: `get_my_address` reads one of the agent's own live addresses, as `GET /v1/addresses/{addressId}` (#575).

### Changed

- Registration and ownership limits are constants in the mailbox Worker (`src/limits.ts`), no longer Worker settings
  (owner decision on #365, 2026-11-25): 10000 self-registered agents in total, 1000 registrations per hour globally and
  100 per source address per hour (self-registered and human-owned agents together), and 10000 agents per human.
  Self-registration is always on; maintenance mode and read-only recovery still refuse new agents. The demo variables
  `REGISTRATION_ENABLED`, `REGISTRATION_MAX_ACCOUNTS`, `REGISTRATION_PER_IP_PER_HOUR`, `REGISTRATION_GLOBAL_PER_HOUR`
  and `OWNED_AGENTS_PER_HUMAN` are no longer read and can be deleted after the deploy.

### Fixed

- An unexpected error behind a `503 storage` answer is now logged (`storage-unavailable`, with the error class and, for
  D1 errors only, the first 300 characters of the D1 message), so the cause of a storage outage can be read from the
  Worker's logs. Answers are unchanged. Other error messages, and `D1_TYPE_ERROR` (which can quote a bound value), are
  not logged.

## 0.1.0 — 2026-11-24

First versioned release. It covers everything merged after the last production deploy (`4fae8d3`, #531) up to and
including #569. No published HTTP or MCP contract is removed or changed incompatibly; every change below is additive.

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
  `revoke_my_address` (#566), `update_my_address` (#567), `get_my_receipt` (#569).
- `/account` pages through older recent actions (#536).

### Fixed

- Publishing describes and refuses media plainly for text-only accounts (#532). The refusal reason strings for those
  accounts changed; apps that match the old text literally must update.
