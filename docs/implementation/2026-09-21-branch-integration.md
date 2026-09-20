# Retained branch audit and registry integration

Baseline: main `068ccb81d2d18abe34d5fd5ef92b44576c5c47fc`, audited 2026-09-21.
There were 15 non-main branches before this work, not 15 missing features.
The owner requested complete integration and a return to small foundation fixes.
The active work order is now [PLAN.md](../../PLAN.md), not historical handoff lists.

## Inventory and disposition

| Retained branch | Tip (short) | Baseline finding / resolution |
| --- | --- | --- |
| claude/build-weave-orchestrator-bMNiH | 6ef3eecc | Full tree already exists in main history; reconcile ancestry without restoring old layout. |
| claude/continue-handoff-work-U1Z7b | daa46234 | Full tree already exists in main history; reconcile ancestry without restoring old layout. |
| feat/agent-only-http-surface | 4c1d1a56 | Already an ancestor; no missing commits. |
| feat/file-write-approval-v0 | de92fa3c | Explicitly superseded #104; retain, do not import the second approval model. |
| feat/m1-durable-approval | 3bb21f32 | Already an ancestor. |
| feat/m1-governed-http-entry | f833c77d | Already an ancestor. |
| feat/operator-review-screen | 31c6c5af | Already an ancestor. |
| feat/reviewed-approval-decisions | b2efd505 | Already an ancestor. |
| feat/simple-echo-plugins | a2ddd73c | Already an ancestor. |
| feat/typescript-rust-clients | adb4df14 | Explicitly superseded #108; retain, do not import SDK products. |
| feat/verified-approval-review | cc3b52c2 | Already an ancestor. |
| feature/operation-authority | e51701b0 | Recover registry snapshot/revalidation details; preserve the newer main implementations elsewhere. |
| fix/dependency-review-evidence | 5e2d2ec2 | Already an ancestor. |
| fix/direct-http-connection-isolation | 40b3755d | Already an ancestor. |
| fix/fail-closed-token-revocation | 8da90bb5 | Already an ancestor. |

The one-time audit exported Git history using contents-read only, with no persisted
checkout credentials and no execution of retained branch code. Its workflow is
removed from the final tree. Run35541444561/artifact10614882036 archive SHA256:
`ed7f3c8148e574e33f54ab729b2c0e2e8a434319d86dec76511a58e74276b1c9`.
Native git ancestry, tree and patch comparison were checked independently from the
connector's ahead/behind summaries. Branches and rejected prototypes are not deleted.

## Exact historical equivalence

`6ef3eecca243706bda1a25d1e4af02bffec42c61` has tree
`270fb9b3e3300cb6a6907c69946d9239acf7e516`, exactly equal to main ancestor
`d4cf83465c0ac3d9e244a51ab6ad645e4c29f39b`.

`daa462340ece86f0d01945002ec796a03c0799d5` has tree
`ed8accd3be9cba1400f2c922296ab05aab200793`, exactly equal to main ancestor
`ab0fed7eda873ff81ef4f6acd0a6d48b71e8be99` (#56). The prior operational-hardening
snapshot849bc372 also equals main ancestor4ce51f3e (#55).

Recording these histories in the integration does not apply their old tree over
main. Their entire contents were already incorporated, not simply assumed so from
a closed PR or a matching title. No unreviewed changed file is hidden by that decision.

## Operation-authority branch resolution

Compared all changed paths against current main. The older ToolCapability namespace,
argument order and list wire shape are replaced by the current feature-owned
contracts. Exact grants, denied bare grants, normalized adapters, immutable request
snapshots, explicit CLI choices and manifest capability propagation already exist.
Current templates are narrower for CLI exec; preserve them. Current DirectHttp
also verifies the registered full handle and invocation identity, so do not restore
the older weaker handle checks or introduce its redundant connection record.

Current ToolActor additionally owns durable attempts, approval and leak checks.
Keep those, current tests/fixtures and current CI with real dependency evidence.
The old branch-specific source-import/validation workflow and obsolete plan are not
added. Existing bounded MCP diagnostics are retained; neither diagnostic version
proves the #92 transport failure fixed.

Two useful details had not survived the overlapping implementations:

1. Registry access changes must snapshot all candidate tools and grants before
   mutating either map. GrantTools used to replace tools before enumerating new
   grants; failure could combine new availability with old authority. ConfigureAccess
   used to clear input aliases and leave partial state after a later bad row.
2. ResolveAsync must recheck registered connection availability after awaited schema
   discovery, in addition to current grants and definition, before minting a token.

Both are adapted to current APIs, not copied with obsolete contracts. Unlike the
old candidate, missing capabilities remain explicit empty grants and orphan grant
rows are not introduced. The implementation changes only those two registry files.

## Regression and limits

Seven focused cases exercise input-failure consistency, self-aliasing, an invalid
later row, owned/deduplicated replacement and connection removal/disconnection while
schema lookup is blocked. Test-only b32a128/CI35541715620 executed all seven: six
intended failures and one positive control; the full suite had2742 passes plus those
six failures in nine TRX reports. Artifact10614618145 SHA256:
`f68ff155beaabc3fc1c40f40a95be2537156cef30d6089f9c7b2ca5748d3cfa1`.
Exact final and merged-main results belong on PR #114, not assumed from this red run.

The tests operate on real registry state; a deliberately failing enumerable models
input capture failure and a controlled tool actor models the schema-await boundary.
They do not establish multi-threaded atomic dictionaries, durable rollback after a
failed actor-state write, resource-binding revision isolation or a new Orleans
reentrancy guarantee. Final ToolActor authorization still applies before execution.
No new dependencies, persistence schema, authority vocabulary or SDK are involved.

#92 and #110 retain their separate acceptance work. No release, deployment, production
credentials, state reset or deletion of retained history is performed here.
