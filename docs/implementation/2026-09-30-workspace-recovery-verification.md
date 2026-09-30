# Verify current services before confirming workspace recovery

## Complete recovery path

The installed MCP recovery path now completes **observe → explicitly restore → verify → confirm**. Returning successfully from reconnection alone no longer permits workspace confirmation.

Use the existing runtime observation and reconciliation operations, frozen resource/service digests, exact connection grants and fresh management ID described in [MCP restoration](2026-09-30-workspace-mcp-restoration.md). No new endpoint, request field or grant is added.

After supported MCP restoration succeeds, reconciliation:

1. Revalidates all current reconciliation, installation and tool connection grants.
2. Obtains a fresh [service observation](2026-09-30-workspace-mcp-observation.md) against the frozen service plan. This verifies both pinned peer metadata and the tool's current connection to the exact installation.
3. Reobserves retained runtime resources, so a resource that stops while service verification runs prevents confirmation.
4. Rechecks current grants, cancellation and both frozen plans before persisting confirmation and completing the mandatory journal record.

The response's existing `observation.hostedServiceObservation` contains the final service diagnostic. `observation.observedAt` records completion of these sequential observations; it is not an atomic snapshot timestamp.

| Final check | Result |
| --- | --- |
| Services `Ready`, resources ready, plans/current authority valid, state and completion evidence confirmed | `Confirmed`, `hostedServicesRestored: true`, with the checked service observation. |
| Services `NotReady`, `Unknown` or any unrecognized condition | `Blocked`, `reason: hosted-services-not-ready`, with per-installation diagnostics; no confirmation write. |
| Resources unhealthy after service verification | `Blocked`, `reason: runtime-resources-not-ready`; service diagnostics remain in the observation. |
| Either frozen plan changed | `PlanChanged`; no confirmation write. |
| Caller cancellation, authority revocation or an exception after restoration began | Admission remains unknown; no automatic retry or claim of no effect. |

Existing reconnection failures keep their specific reasons. They do not perform the additional final service verification. Resource-only workspaces retain their existing path and omit service observations.

## Preserved boundaries and limits

Installation desired state, persisted field IDs, enum values, actor keys, resource IDs, request digests and journal admission semantics are preserved. Invocation grants are unchanged; no business operation, approval consumption, provisioning, automatic restoration or invocation replay is added.

A blocked confirmation can leave a newly established connection in place. A journal `Failed` outcome records that this reconciliation could not confirm recovery; it does not mean reconnection had no effect or was rolled back. Reusing its retained management ID returns `AlreadyAdmitted`. Inspect current diagnostics and explicitly choose a fresh plan and ID for further recovery.

Observations across actors, peer metadata and resources are sequential and can become stale after their checks. Confirmation describes one bounded operation on this host, not continuous health, a lease, a distributed transaction or successful future tool execution. Supported restoration remains installed loopback HTTP MCP tools; Agent, Dapr and unsupported service profiles remain blocked.

## Verification scope

Regression tests first demonstrated that final service checks were omitted and unhealthy services still reached confirmation. Real HTTP/Orleans tests with preserved SQLite files and an actual MCP peer exercise both maintained protocol revisions. A test-only boundary changes the peer contract or disconnects the actual tool immediately after the real restoration implementation returns, making the final-check failure deterministic. They verify blocked confirmation, retained failed evidence through another host restart, duplicate admission without reconnection, explicit recovery with a fresh ID, healthy response diagnostics and zero `tools/call` requests.

Unit checks cover positive verification before the state write, unhealthy/unknown/unrecognized service conditions, resource loss and plan changes during verification, revocation, cancellation and probe exceptions. Full-suite results and prerequisite skips are recorded in the PR for its tested commit. This is self-review and boundary regression evidence, not a claim of continuous health or independent security review.
