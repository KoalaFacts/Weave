# Completed-only releases no longer pause the demo

## Change

Every ordinary demo release (`deploy-weave-worker.yml`, `publish/demo`, the owner comment) runs in
`completed-only` mode. Until now it still deployed the maintenance version first, then validated the
database, uploaded the recovery version, and deployed the active version. In the 2026-11-05 release, maintenance
ran from 09:00:59 to 09:02:01 UTC: about a minute of 503 responses.

That pause protected nothing in this mode:

- `completed-only` changes no schema and no data. Its only write-capable statement is the
  `CREATE TABLE IF NOT EXISTS data_migrations` check, which must report zero changes.
- So the version already serving is compatible with the database before, during and after the release.

`scripts/deploy.ts` now skips the maintenance deployment in `completed-only` mode.

## Preserved

- **Still pauses first:**
  - `maintain` mode (the separately approved schema migration workflow);
  - the one-time first-party OAuth bootstrap, which adds bindings.
- **Same completed-only checks.** The ledger record and checksum, the absent write gate and the empty schema
  plan are still checked at preflight, before the ledger check, after it and before activation. The restricted
  metadata interface and the zero-change ledger evidence are unchanged.
- **Recovery evidence.** The recovery version is still uploaded, verified and recorded before activation.

## Deliberately changed

- **On failure.** A completed-only failure before activation now leaves the previous version serving instead
  of maintenance. The report's phase says `previous version still serving`.
- **Stage labels.** The validation stages are now `preflight` and `before-ledger-check` (were
  `before-maintenance` and `maintenance`).

## Verification

`test/deploy.test.ts` (99 tests):

- a completed-only release deploys only the recovery and active versions, and never serves `MAINTENANCE_MODE=true`;
- concurrent ledger, schema or write-gate changes after preflight still stop the release before any deployment;
- completed-only with the OAuth bootstrap still deploys maintenance first.

Mutation checks:

- always pausing fails 15 tests;
- pausing only in maintain mode, which drops the bootstrap condition, fails 1.

## Limits

The checks do not lock out a concurrent administrator, as before. Without a pause, the old version also
serves traffic during validation. That is the same version and the same schema, so it changes nothing about
what the checks prove.
