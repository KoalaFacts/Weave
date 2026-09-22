# Mainline foundation plan

**Updated:** 2026-09-22. **Baseline:** `784461759315754544685b492308062d33f8f431`.
**Goal:** make one governed operation reliable before widening the product.
**Architecture:** [ARCHITECTURE.md](ARCHITECTURE.md) owns the target;
[AGENTS.md](AGENTS.md) owns implementation rules. This document owns the current
work order. Historical handoffs are evidence, not competing live roadmaps.

## Scope we are keeping

C# owns authority, approval, Invocation/Attempt and durable outcomes. Features stay
directly under src, with Vertical Slice + Composition and no new universal layer.
Python, TypeScript, Rust and Java have one small [Echo plugin](examples/plugins/echo/README.md)
each. That is enough for the initial language story; do not restart SDK/CLI product
work, another approval store, a marketplace or a general workflow engine.

Existing tested capabilities are operation-scoped grants; file-backed invocation
admission/deduplication; exact-plan filesystem approvals and independent reviewed
decisions; governed HTTP and AgentOnly routing; readable review; fail-closed token
revocation; and exact-source NuGet dependency evidence. These are bounded features,
not complete tenant isolation, tamper-proof storage or distributed exactly-once.
The Dashboard review screen remains read-only. Approvals do not grant execution
rights, and unknown outcomes do not authorize replay under a fresh ID.

## Integration closure

The [branch audit](docs/implementation/2026-09-21-branch-integration.md) records every
retained non-main branch at its baseline. PR #114 reconciled thirteen original
tips and recovered two registry safeguards without restoring obsolete executors,
wire shapes or workflows. The explicitly rejected #104 alternate approval and
#108 SDK branches stay excluded. A generic instruction to integrate work does not
reverse those scope decisions. Never use an unchanged-tree merge to hide a diff.

PR #124's response hardening merged as dea58f06 and was verified on actual main.
PR #123's [dependency integration](docs/implementation/2026-09-22-dependency-pr-integration.md)
merged as25694f69 with actual changed-package evidence. Its seven applicable
updates were integrated; #119's incompatible compiler proposal was rejected.
PR #125 merged as605d114d, passed actual main verification and closed #92 with
causal positive/negative controls. PR #126 merged ascbfff7db, passed actual main
verification and closed #110's remaining acceptance. PR #127 merged as78446175
with retained-storage recovery and actual main verification. No extra workstream
was added; do not repeat completed recovery work because of an old checkbox.

## Ordered work and exit gates

### 1. Registry consistency and integration (completed in #114)

**Files:** ToolRegistryState.cs and ToolRegistryActor.cs under
extensions/Weave.AgentRuntime/PluginConnections; focused tests under
tests/Weave.Agents.Tests/ToolRegistrySnapshotRecoveryTests.cs.

- [x] Prove failures before code changes: input enumeration fails after one list is
  read; configuration aliases the current maps; a later input row is invalid; a
  connection disappears or disconnects while schema lookup is awaiting.
- [x] Implement complete candidate snapshots before mutating either map.
  Preserve owned lists, ordinal deduplication, removal of old grants, deny-only
  missing capabilities and the current exclusion of orphan capability rows.
- [x] Recheck the registered connection after schema lookup, alongside current
  authority and definition, before minting a token. Do not infer invocation rights
  from tool availability or an old connected state.
- [x] Merge the reviewed branch resolution only after full regression, strict build,
  format and real dependency evidence pass; verify the resulting main as well.

This is input-failure consistency within the existing actor, not a concurrent-map
transaction or a fix for failed persistentState.WriteStateAsync. Durable write
failure semantics need their own real storage boundary test before being claimed.

### 2. Transport boundaries and reliability (completed in #124 and #125)

**Inspect:** extensions/Weave.Mcp/InvokeTool/{HttpMcpTransport,McpConnection}.cs,
examples/echo-mcp/server.py and tests/Weave.Tools.Tests/EchoMcpHttpTransportSmokeTests.cs.

The bounded response hardening in **PR #124** is separate from the original
before-header failure. See [scope and migration notes](docs/implementation/2026-09-21-mcp-http-boundaries.md).

- [x] Reproduce native redirects/cookie reuse, incomplete whole-request deadlines,
  active-read disposal, queue blocking, UTF-8/delimiter/frame miscounts, invalid
  decoding and incomplete-event promotion against unchanged production code.
- [x] Implement endpoint retention, no implicit cookies, a linked whole-request
  deadline, disposal cancellation, bounded raw body reads and strict frame decoding.
- [x] Complete exact-head regression, security/dependency gates, review, integration
  and actual merged-main verification for #124.

**#92 is closed after #125's reviewed merge and actual main verification.**
See [cause, controls and limits](docs/implementation/2026-09-22-echo-http-close-lifecycle.md).

- [x] Reproduce before-header ResponseEnded using the real handler and native
  client: a completed JSON/204 response was reused before the peer's close arrived.
  Observe the queued undispatched POST, live server and bounded runtime diagnostics.
- [x] Retain successful controls and header-removal negative controls. Check every
  result and count calls so a retry cannot disguise a dropped or duplicated effect.
- [x] Announce the server's existing close policy, without client pooling/retry or
  protocol changes. Run ten distinct mixed JSON/SSE calls per positive case and
  the full suite; retain the original smoke tests and all earlier failure evidence.

The demonstrated example/native-client mechanism is not a guarantee against every
future network EOF or proof of every historical occurrence.

### 3. Supply-chain evidence acceptance (completed through #126; #110 closed)

**Inspect:** .github/workflows/ci.yml, scripts/check_dependency_review_evidence.py,
scripts/check_dependency_license_evidence.py, scripts/submit_nuget_snapshots.py,
the independent Dependency Policy Acceptance workflow and their tests.

- [x] Exercise a real dependency-changing review with both exact source graphs,
  actual changed package versions and policy evaluation, not only empty deltas.
  #123 also repaired the complete-response parsing assumption without weakening
  missing-evidence, schema or resource bounds.
- [x] Demonstrate denied/unknown-license and missing-evidence behavior using the
  pinned action and isolated API fixtures. Preserve the original policy and block
  missing/unlicensed output with a narrow evidence gate, not a second SPDX engine.
- [x] Exercise the actual snapshot API with a contents-read Actions token: require
  403, nonzero producer result and zero confirmed submissions. Document a reviewed
  same-SHA maintainer integration path without fork-token escalation.

Normal #126 review/merge and actual merged-main verification are recorded on #126
and #110. Isolated fixture results are not invented production dependency evidence.
The permission test is fork-equivalent, not a live external fork or a verification
of every administrative setting.
See [acceptance and maintainer procedure](docs/implementation/2026-09-22-license-fork-acceptance.md).

Keep source-lock validation read-only and the snapshot writer pinned/isolated from
PR build execution. A submission receipt is not a policy result. No new producer,
write permission, package downgrade or license-policy exemption is needed here.

### 4. One-host recovery and safe onboarding (current stage)

**Inspect:** Authority/Tokens, Invocations, the SQLite extension and Host composition.

- [x] Finish retained-storage recovery in #127: optional required-existing startup
  for both owners, no runtime empty-journal creation, and real Host tests preserving
  approval, revoked credentials and unknown outcomes. Reviewed merge and actual
  main evidence are recorded on #127 at baseline78446175.
- [x] Test behavioral expiry/cancellation under the injected clock and actual OS
  process shutdown/restart in #128. Five clock cases plus one parent scenario
  exercise graceful completion and a killed-after-effect process, retained unknown
  state, expired/revoked authority denial and no repeat dispatch. Full regression
  passed on947ba01; exact final review/merge/main evidence belongs on #128.
- [ ] Provide one explicit trusted provisioning/operator route to the same logical
  backend. AgentOnly must not acquire management endpoints; separate SQLite files
  must not be presented as a shared approval system.

The [storage recovery record](docs/implementation/2026-09-22-single-host-storage-recovery.md)
distinguishes explicit first initialization from recovery. Required-existing flags
do not detect erased/rolled-back evidence or a fully valid empty replacement.
The [lifetime/process record](docs/implementation/2026-09-22-single-host-lifetime-recovery.md)
distinguishes deterministic expiry timers from fixed validation timestamps and an
actual controlled OS-process kill from physical power loss. Tool configuration is
reconnected by trusted fixture setup, not claimed durable/public provisioning.
The existing internal operator interface is not new public onboarding. The final
provisioning/operator subitem remains the next task; do not expand the plan.

Do not add multi-host consensus, resource provisioning or broad UI redesign to
these checks. Choose the smallest failing case each time.

## Working and verification discipline

Use one short-lived change based on current main, not a stack of unfinished
alternative designs. Main is the shared integration baseline; protected main is
updated by normal expected-head PR merges, not direct or forced pushes.

For each slice: reproduce a failure, keep positive controls, implement minimally,
review the diff, run relevant real-boundary tests plus the full suite and preserved
security/format gates, inspect actual artifacts, then verify merged main. Report
first-run failures even if a repeat succeeds. No automatic unknown-effect replay.

Run from the repository root (focused MTP options follow the current runner):

```bash
python3 -m unittest discover -s scripts/tests -v
dotnet restore Weave.slnx --locked-mode
dotnet build Weave.slnx --no-restore -c Release /p:TreatWarningsAsErrors=true
dotnet test --solution Weave.slnx --no-build -c Release
```

Samples retain their independent matrix; ordinary backend work does not require
all four language toolchains. Record exact test/merge evidence on the PR, not as
permanent rules here. No deployment, release, production credential change, data
reset or branch deletion is implied by this plan.
