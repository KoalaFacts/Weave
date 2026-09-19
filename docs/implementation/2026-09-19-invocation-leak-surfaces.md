# Invocation leak surfaces — 2026-09-19

## Scope

A bounded prerequisite for M1's durable invocation work, based on PR #97 at
`5b6c2ce5584d7c4a625b3c2fa958f4ea17d6925d`. Only
`src/Invocations/InvokeTool/ToolInvocationLeakGuard.cs` changes production behavior.
No package, project, workflow, public contract, storage or authorization rule changes.

PR #98 independently owns durable Invocation/Attempt implementation. A duplicate
test-only PR #99 was closed unmerged when that concurrent work was discovered;
its branch was preserved. This change neither edits that implementation's branch
nor introduces a competing invocation ID or persistence contract.

## Confirmed defects

- Outbound scanning selected RawInput instead of Parameters when RawInput was
  non-null. Benign or empty raw input could therefore mask a recognized secret in
  an independently dispatched parameter.
- Failed tool results skipped inbound scanning entirely.
- The Error field was never scanned, even when a result was otherwise successful.

## Behavior

Both outbound input surfaces must be safe before substitution or dispatch.
An already-detected raw-input leak may short-circuit further scanning because the
entire invocation is blocked. Otherwise Parameters is also scanned through the
existing source-generated dictionary serializer. The original caller input is
not modified and the existing single sanitized blocked event is retained.

For inbound responses, Output and Error are checked independently, regardless of
Success. Only a field containing a detected leak is replaced with the existing
redaction marker. Clean values, null Error, Success, Duration, ToolName and other
record metadata are preserved. A failure remains a failure; two leaking fields
still produce one sanitized security event. Logging never includes payloads.

Scanner errors are not caught and turned into unscanned results. Existing
ToolActor authorization, parameter ownership, secret substitution, connection
checks and completion semantics remain. There is no alternate execution route.

## Test-first evidence

Test-only commit `dcfacd0de9b27789b8080b7a07d05a99d6086488` added fourteen cases in
`tests/Weave.Tools.Tests/InvocationLeakSurfaceTests.cs` without changing production.
CI run `35445136306` built the code and reported **2,470 passed, 8 failed, 0 skipped**.
All eight failures were the intended response-field or masked-parameter cases;
the prior 2,464 cases and six new positive controls passed. All nine TRX reports
were checked, including framework counters (zero framework errors).

The tests use the real ToolActor, token service, authorizer, LeakScanner and local
event bus with a deterministic connector substitute. The AWS example is synthetic
fixture data already used by the repository, not a live credential. Assertions
check exact field values, unchanged outcome metadata, one local security event,
and the absence of connector dispatch and secret substitution for blocked input.

The final exact-commit CI, formatting, dependency scan and TRX results belong in
PR #100. A failing test-first run is evidence of the defect, not release evidence.
.NET verification uses the existing GitHub CI; no local SDK or relaxed replacement
workflow is assumed. No test assertion, vulnerability warning or format gate is
suppressed to obtain a pass.

## Remaining limits

This expands coverage of the existing detector; it does not prove arbitrary secret
recognition, resistance to encoding/evasion, or complete credential isolation.
It does not sanitize a connector's own internal logs or turn redaction events into
a durable audit journal. Tenant/Room isolation, resource and installation routing,
durable outcomes and approval remain separate work. Review is author self-review,
not independent security certification. No main merge, publication, deployment,
production credential change or persisted-data reset is included.
