# Audit subscriber lifecycle — 2026-09-19

## Why this change belongs to the current increment

While finishing PR #97's exact-operation authority path, CI run `35442581953`
failed during SiloFactory/WebApplicationFactory fixture cleanup. All 2,456 test
cases had passed, but the Silo TRX run summary contained a framework error.
The stack trace reached `CapabilityAuditSubscriberHostedService.StopAsync`,
which called `Cancel()` on a source already released by `Dispose()`.
Passing individual cases did not make that run successful.

The service also allowed repeated startup to overwrite its only subscription
handle, did not unsubscribe when disposed without StopAsync, and let previously
captured callbacks access the released source. These were existing lifecycle
defects, not reasons to relax the operation-authority tests or CI.

## Scope and ownership

Only `hosts/Weave.Host/Audit/CapabilityAuditSubscriberHostedService.cs` changes
production behavior in this follow-up. The service owns its subscription and
stopping source as one lifecycle:

- Startup is idempotent while active: it registers one subscriber.
- A short gate protects subscription admission and source ownership transfer.
- Exactly one stopping caller takes the source and subscription; subsequent
  StopAsync/Dispose calls do not cancel or dispose that source again.
- Cancellation and subscription cleanup run outside the gate, with disposal in
  a finally-protected scope. Exceptions are not swallowed.
- Event callbacks take their own linked source while the parent is still owned;
  callbacks first entering after shutdown do not touch disposed state or write.
- A stopped instance cannot be restarted. A new host needs a new instance.

Existing store calls, bounded retry/backoff, logging and metrics remain. This is
not durable audit, a shutdown drain barrier, or exactly-once delivery. A callback
admitted before stopping can already be writing; synchronous writes cannot be
undone by cancellation. The planned mandatory pre-dispatch Invocation evidence
is a separate product slice, not implemented by this hosted subscriber.

## Regression evidence and verification

Test-only commit `aa126fccf172f8b65b34326f05c0ad6a414c6333` added eight cases in
`tests/Weave.Silo.Tests/Audit/CapabilityAuditSubscriberLifecycleTests.cs`.
Run `35443353223` compiled them, passed the existing 2,456 cases, and failed all
eight new cases for the expected behaviors. The downloaded TRX has zero framework
errors in that red run. Cases cover repeat startup, repeated stop/disposal,
disposal without stop, callbacks captured before stop/disposal, attempted restart,
and concurrent stop/disposal. Captured callbacks are invoked directly so an event
bus's exception logging cannot turn a failing callback into a passing test.

Production fix: `d9f8da6ace9afce8ba368f22a4c047fa48c32be2`.
Final exact-commit CI/test results belong in PR #97's verification record. Verify
TRX ResultSummary outcomes and framework errors as well as passed/failed cases.
Use the existing CI, dependency-security scan, formatting and full .NET suite;
no new package, project, workflow, skipped tests or suppression is introduced.

Review is an author self-review against the repository rules, not independent
security certification. The external-agent, Tenant/Room, resource-revision,
approval and durable-attempt limits in the exact-operation migration record
remain unchanged. No main merge, release, publication or deployment is included.
