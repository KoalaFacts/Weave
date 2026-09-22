# Single-Host retained-storage recovery

This is the first storage-recovery item in PLAN.md stage 4, not completion of the
later clock/worker-lifecycle or trusted external provisioning work. It keeps the
existing token service, invocation journal, approval model and AgentOnly routes.

## Initialization is not recovery

Two opt-in settings belong to their existing storage owners:

```json
{
  "CapabilityTokens": {
    "RequireExistingStorage": true
  },
  "Weave": {
    "Invocations": {
      "RequireExistingStorage": true
    }
  }
}
```

This is an overlay, not a complete deployable configuration. Configure the existing
SigningKey, protected persistent RevocationDirectory and DatabasePath separately;
no default signing credential is supplied. Both flags default to false to preserve
the existing first-time initialization behavior and storage paths. A trusted initial
setup can create the stores; afterward, configure both flags true for normal retained
startup and recovery. Do not turn them off to hide missing history.

Required journal startup opens the existing file without creating it and probes
all current journal/approval columns before changing journal mode. Missing files,
empty/unrelated databases, missing columns/tables, directories or invalid SQLite
content block startup. It does not rebuild missing tables during recovery. Normal
journal reads, admission and completion also open in ReadWrite mode, even if the
service was initially bootstrapped with creation allowed. A missing live journal
therefore fails recording instead of creating a misleading empty substitute.

Required token startup checks that the configured revocation path remains an
accessible directory rather than calling CreateDirectory. The Host resolves both
the journal and token service before it accepts work; the check is not deferred
until the first token-bearing request. Existing signature, expiry, revocation,
independent-reviewer and execution-grant checks remain unchanged.

## Recover with evidence, not by emptying it

Stop or quiesce the affected Host before restoring storage. Preserve the actual
SQLite database and its associated WAL state, as well as the revocation directory
and signing configuration. A live WAL is part of committed database state; copying
only the main file or deleting sidecars can lose evidence. Use an appropriate
SQLite backup procedure or a verified stopped-state copy, not the file moves used
by these isolated tests. Do not run two active Hosts against separate files and
call that shared approval state.

Restore the retained stores to their configured paths, keep both required-storage
flags enabled, then start the Host. A startup failure is an operational failure,
not permission to create a new database, clear revocation markers or mint replacement
credentials. Protect these stores outside Agent-readable/writable tool roots.

Runtime loss blocks new durable admission. Restoring the original quiescent journal
allows its previous call IDs to be queried and deduplicated; a failed admission does
not erase another call's history. At restart, pending approval stays pending and
consumed approval stays consumed. A separate trusted operator may decide through
the existing internal actor interface to the SAME Host; the original caller still
needs its retained ID, original inputs and current execution authority to resume.
No operation is automatically replayed by startup or recovery.

An effect whose completion could not be recorded remains OutcomeUnknown after
restart. Restoring storage or dropping a test fault does not make it safe to repeat
the effect. A revoked credential remains rejected after reconstruction; unrelated
valid credentials are not invalidated merely because the Host restarted.

## Verification and entry boundaries

The shared fixture repeatedly creates and disposes complete WebApplicationFactory
Host/Orleans/HTTP components against the same real SQLite/revocation files. It uses
in-memory actor state, so tool connections must be deliberately re-established.
These are not separate OS processes, abrupt kills or power-loss simulations. The
later worker-shutdown/restart plan item remains open.

Twelve cases cover missing and replaced stores, unchanged invalid evidence, runtime
journal loss and restoration, retained approval/revocation, and a real file effect
with failed completion recording. The corrected test-only revision produced seven
intended failures and five positive controls; exact final results belong in #127.
No journal, capability validator or filesystem effect is mocked.

After recovery, AgentOnly still does not register management or approval-decision
HTTP routes. Trusted fixture setup and review call the existing backend interface,
not a new unauthenticated endpoint. Authentication is disabled only in this isolated
HTTP fixture so its capability checks are exercised independently; deployment still
requires the existing authentication, transport and exposure controls. This increment
does not deliver public operator onboarding, human login or a production sandbox.

## Deliberate limits

RequireExistingStorage proves presence and current column compatibility, not that
this is the original unmodified store. It does not detect an attacker replacing it
with another fully valid empty database/directory, deleting individual revocation
markers, rolling back a backup or manipulating table constraints/history. There is
no new store identity, checksum schema, distributed consensus or tamper-proof audit.
The WAL/open settings can create SQLite sidecars during legitimate recovery; the
claim is no empty main-database/schema replacement, not zero filesystem writes.

With the flags false, a fresh process may initialize absent storage. That remains
bootstrap, never evidence that lost history was recovered. No production storage,
credential rotation, deployment, backup restore or data reset is performed by this
change. Defaults, token format, package graph and persisted schema are unchanged.

References: [SQLite connection modes](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings),
[SQLite WAL](https://www.sqlite.org/wal.html), [SQLite backup](https://www.sqlite.org/backup.html).
