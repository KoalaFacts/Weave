# Single-Host lifetime and process recovery

This is the second existing stage-4 gate in PLAN.md. Retained-storage startup and
runtime behavior from #127 is preserved; no public provisioning route, SDK, package,
new Host executable or database schema is introduced by this increment.

## One clock for credential expiry and linked cancellation

CapabilityTokenService already used its injected TimeProvider when minting and
validating ExpiresAt. MintLinked instead scheduled CancelAfter against system time.
Advancing a deterministic provider therefore made validation reject the credential
without notifying its active linked work to cancel.

MintLinked now creates a provider-backed expiry CancellationTokenSource and links
that source with the parent token. The existing CapabilityTokenSource owns cleanup:
its disposal unregisters the source, disposes the expiry timer/source and disposes
the linked source. Failed linking disposes the expiry source before rethrowing.
No signing, token serialization, grant, revocation or default-lifetime behavior is
changed. With TimeProvider.System the public behavior remains ordinary timed expiry.
The supported timer range remains the runtime's CancellationTokenSource range.

Five focused cases use the existing FakeTimeProvider dependency in the security
test project. They check cancellation exactly at expiry, independent clock/token
isolation, disposal before expiry, parent cancellation and explicit revocation.
The first two reproduced failure before the implementation, while the other three
remained positive controls. No wall-time sleep is used to establish expiry.

This is cooperative cancellation for MintLinked consumers, not instantaneous
revocation of a remote process. A plain serialized capability does not transport
an in-process CancellationToken. Expiry and revocation must still be validated at
execution/authorization boundaries, and cancellation cannot undo an external effect.

## Real process stop and restart

HostProcessRecoveryTests uses the existing test assembly as a child-process fixture.
The filtered child test starts the actual Host via SiloFactory, with real Kestrel,
Orleans composition, signed capability validation, FileSystem connector and SQLite
journal. It binds HTTP to loopback port zero and announces readiness atomically.
The test's explicit configuration and synthetic key stay in its isolated temporary
folder; no production configuration, credential or data file is touched.

One outer test covers two scenarios, each with an initial process and a fresh
recovery process:

1. A file write completes and is recorded. The parent asks the child to stop,
   waits for normal process exit, edits the target externally, and starts recovery
   with both RequireExistingStorage flags. The completed outcome and replay
   metadata must survive, without overwriting the external edit.
2. A test-only forwarding connector performs the REAL filesystem write, records
   that dispatch was observed, then holds its response before completion recording.
   The parent observes the file effect and pending HTTP task, kills that OS child
   process tree and waits for its exit. A new process opens the retained journal;
   the admitted attempt must remain OutcomeUnknown and repeated submission must
   not dispatch or write again.

Both scenarios advance the validation clock before the recovery process starts.
The original short-lived credential must return401, an explicitly revoked one
must still return401, a different subject's outcome query must remain404, and a
currently valid original-subject credential can inspect retained metadata. AgentOnly
must continue returning404 for management and HTTP approval-decision routes.
The test counters and final file contents independently check one dispatch, rather
than inferring non-execution from an HTTP status alone.

The Host suite uses its already-present ApprovalClock for fixed timestamps; it
does not gain a FakeTimeProvider package. Timer firing is established separately
by the security tests, not by this timestamp-only process fixture. Test process
output is drained concurrently and retained in bounded tails; the synthetic key
is redacted before a diagnostic failure. Process arguments contain only the test
assembly/filter, not credentials. Normal and exceptional paths wait for child exit.

## Limits that remain

The interrupted process is the Host-containing test process, not an isolated
production resource provider. Actor configuration remains memory-backed in this
fixture, so the trusted setup reconnects the FileSystem tool on each boot. The
journal and revocation store are retained; neither is synthesized from a success
response. The controlled hold establishes a specific crash window, not proof of
all possible timing races or OS power-loss durability. It does not simulate torn
filesystem writes, a lost disk, a machine reboot or multi-Host consensus.

No automatic operation retry, fresh invocation ID, lost-outcome deletion, permission
fallback or public administrator endpoint is added. Protected deployments must still
retain and protect their actual stores and signing configuration. The safe public
provisioning/operator path remains the next existing PLAN gate, not completed here.

## Verification record

The test-only revision3e643de reproduced two intended clock failures with2794 passes
and no skipped/framework errors. The first process-test build referenced a clock
package absent from that project; it was corrected by using the suite's existing
clock, not by adding a dependency or skipping the scenario. Exact source, CI results,
review and eventual merged-main evidence are recorded on #128. A passing child
exit alone is not the acceptance criterion; the parent performs all HTTP, retained
outcome, effect-count and stale-authority assertions.

Reference: [CancellationTokenSource(TimeSpan, TimeProvider)](https://learn.microsoft.com/en-us/dotnet/api/system.threading.cancellationtokensource.-ctor?view=net-10.0).
