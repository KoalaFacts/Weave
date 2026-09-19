# M1 prerequisite: tool request boundary — 2026-09-19

## Scope

This bounded change starts from `280c90f5e20718b553723529be51fab991f581a0`.
It fixes the existing invocation path before adding exact-operation authority.
It does not implement M1 as a whole, new grants, approval, or a durable journal.
No new project, package, framework, workflow, or public DTO is needed.

## Behavior

`ToolActorIdentity.Ensure` now rejects a connection or invocation whose tool name
differs from the activated/established actor name, using ordinal comparison.
Empty, whitespace, and differently cased requested names do not match. Rejection
happens before connector dispatch; a rejected mismatched rebind leaves the existing
connection intact. A request that establishes an unactivated actor's identity
retains the existing behavior; runtime activation remains the authoritative path.

`ToolActor.InvokeAsync` copies the string parameters into an owned ordinal
dictionary before the first await. Leak scanning and secret substitution consume
that same request snapshot. A direct caller's subsequent dictionary changes do
not redirect this invocation. The caller's dictionary is not modified by the actor.

Existing token signature, expiry/revocation and workspace checks, `tool:<name>`
grants, connector selection, secret substitution, response redaction and completion
events remain in place. No alternate execution route is introduced.

## Test-first evidence

Test-only commit: `d168aed9629683efd555d81e08a60597b1a04daf`.
CI run: https://github.com/KoalaFacts/Weave/actions/runs/35438886207

The unchanged production code compiled with zero warnings/errors. Its full suite
reported 2,403 passed, 11 failed, 0 skipped: all 11 failures were the new mismatch,
rebind, and mutable-input regression cases. The parameter test observed
`changed.txt` where `original.txt` was required. The three new controls for normal
secret handling, wrong workspace, and invalid signature passed.

The tests use the real token service, authorizer and leak scanner with a
deterministic connector substitute. TaskCompletionSource coordinates mutation
across authorization; no timing sleeps, live credentials, or upstream writes.
Final exact-commit results are recorded in PR #96, not inferred from this red run.

After building, the affected suite can be run with:

```bash
dotnet test --project tests/Weave.Tools.Tests/Weave.Tools.Tests.csproj --no-build -c Release
```

## Limits

This is an owned copy of string-valued invocation inputs, not the future canonical,
immutable approval plan. It does not make concurrent mutation during dictionary
copying safe, freeze connection configuration, constrain a trusted connector's own
behavior, or establish Tenant/Room/resource/installation identity. Existing grant
semantics and actor keys are unchanged. Those boundaries need explicit subsequent
slices and tests; neither this patch nor its test counts certify them.
