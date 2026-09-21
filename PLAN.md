# Mainline foundation plan

**Updated:** 2026-09-21. **Baseline:** `35807690bfc12c058253b4f978767beb8af87f8e`.
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
retained non-main branch at the previous baseline. PR #114 reconciled thirteen
original tips and recovered two registry safeguards without restoring obsolete
executors, wire shapes or workflows. Its merge and actual main validation are
recorded at commit35807690. The explicitly rejected #104 alternate approval and
#108 SDK branches stay retained but excluded. A generic instruction to integrate
work does not reverse those scope decisions. Never use an unchanged-tree merge
to hide an unreviewed difference.

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

### 2. Transport boundaries and reliability (current round)

**Inspect:** extensions/Weave.Mcp/InvokeTool/{HttpMcpTransport,McpConnection}.cs,
examples/echo-mcp/server.py and tests/Weave.Tools.Tests/EchoMcpHttpTransportSmokeTests.cs.

The bounded response hardening in **PR #124** is separate from the intermittent
before-header failure. See [scope and migration notes](docs/implementation/2026-09-21-mcp-http-boundaries.md).

- [x] Reproduce native redirects/cookie reuse, incomplete whole-request deadlines,
  active-read disposal, queue blocking, UTF-8/delimiter/frame miscounts, invalid
  decoding and incomplete-event promotion against unchanged production code.
- [x] Implement endpoint retention, no implicit cookies, a linked whole-request
  deadline, disposal cancellation, bounded raw body reads and strict frame decoding.
- [ ] Complete exact-head full regression, existing security and dependency gates,
  review and explicit integration. An implementation record is not a main merge.

The remaining **#92** acceptance is deliberately not checked off:

- [ ] Reproduce the before-header ResponseEnded failure with controlled peer and
  process lifecycle. Capture bounded server/transport evidence without logging
  secrets; distinguish bind/readiness/process-exit, response framing and pooling.
- [ ] Turn its established cause into a deterministic regression, with a successful
  control and a peer that consumes a request then closes. Count calls so retries
  cannot disguise duplicated side effects. #124 adds the latter control but does
  not establish the original intermittent cause.
- [ ] Fix only the proven cause. Run repeated real HTTP/SSE interactions and the
  entire suite without test retries/skips or longer timeouts to mask the problem.

A repeat passing is diagnostic evidence, not closure. Keep #92 open until the
causal regression fails before and passes after its fix. Successful byte-boundary
regressions do not explain a failure that happens before any response body exists.

### 3. Supply-chain evidence acceptance — issue #110

**Inspect:** .github/workflows/ci.yml, scripts/check_dependency_review_evidence.py,
scripts/submit_nuget_snapshots.py and their tests.

- [ ] Exercise a deliberately scoped, non-vulnerable real dependency change in an
  isolated review. Verify both exact source graphs, the actual changed package
  and version, complete pagination and policy evaluation, not just empty deltas.
- [ ] Demonstrate denied/unknown-license and missing-evidence behavior using an
  isolated policy fixture; do not downgrade production dependencies to make a test.
- [ ] Verify behavior when fork permissions cannot submit snapshots and record a
  maintainer-safe path before promising external contribution onboarding.

Keep source-lock validation read-only and the snapshot writer pinned/isolated from
PR build execution. A submission receipt is not a policy result. Do not remove
current restrictions or update a trusted producer pin without source review.

### 4. One-host recovery and safe onboarding (after those gates)

**Inspect:** Authority/Tokens, Invocations, the SQLite extension and Host composition.

- [ ] Test recovery from unavailable/replaced revocation and journal storage with
  retained real evidence. Distinguish initialized-service failure from a fresh
  process recreating an empty directory; never describe the latter as recovery.
- [ ] Test behavioral expiry/cancellation under the injected clock, then actual
  worker shutdown and restart. No stale token replay or dropped unknown outcomes.
- [ ] Provide one explicit trusted provisioning/operator route to the same logical
  backend. AgentOnly must not acquire management endpoints; separate SQLite files
  must not be presented as a shared approval system.

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
