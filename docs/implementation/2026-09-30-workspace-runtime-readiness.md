# Workspace runtime readiness

## Delivered query

The existing `GET /api/workspaces/{workspaceId}/runtime` response adds `readiness.condition` and `readiness.reasons`. It still requires `workspace:runtime:read` for the target workspace. HTTP and Orleans use the same observation and derivation; no new management operation or grant is introduced.

| Condition | Meaning |
| --- | --- |
| `Ready` | The workspace is running on the current runtime instance, its recovery condition is `StartedOnThisHost`, and all retained runtime dependencies are confirmed. |
| `NotReady` | At least one known blocker exists, such as a stopped container, absent network, detached container or required reconciliation. |
| `Unknown` | No known blocker exists, but at least one required observation or recovery condition is incomplete. |

Known blockers take precedence over incomplete observations. Reasons are deduplicated enum strings. The existing network and container fields retain the per-resource observations and IDs.

| Reasons | Source |
| --- | --- |
| `WorkspaceNotRunning`, `NotStartedOnCurrentHost` | Registered lifecycle and runtime instance ownership. |
| `RequiresReconciliation`, `RecoveryConditionUnconfirmed` | Retained recovery condition. |
| `NetworkNotReady`, `NetworkObservationIncomplete` | Recorded network and fresh network observation. |
| `ContainerNotRunning`, `ContainerObservationIncomplete` | Fresh observations of retained container IDs. |
| `ContainerNetworkNotAttached`, `ContainerNetworkObservationIncomplete` | Fresh network attachment observations. |

For example, a running workspace with an exited container reports:

```json
{
  "readiness": {
    "condition": "NotReady",
    "reasons": ["ContainerNotRunning"]
  }
}
```

## In-process runtime and migration

`NetworkRuntimeCondition.NotRequired` is appended to the existing enum. The in-process runtime returns it only for its `local` network marker; an unrelated network ID returns `InvalidIdentity`. A current-host in-process workspace with no retained containers can therefore report runtime `Ready`. Unsupported container or attachment observations remain incomplete and cannot establish readiness.

This deliberately replaces the in-process network observation's previous blanket `Unsupported` result described in the [network recovery record](2026-09-30-workspace-network-recovery.md). Container recovery remains unsupported in that runtime. Existing callers and assertions are updated directly. The snapshot gains an additive field; no Orleans persisted field IDs, actor keys, grants or stored workspace records change.

## Boundaries

The query performs fresh observations and derives the result at completion, using the existing injected clock for `observedAt`. It does not cache or persist readiness, alter registration, clear `RequiresReconciliation`, start resources, restore Agent state or replay invocations. A recovered container does not by itself establish workspace readiness after a Host restart.

This is **runtime resource readiness**. It does not establish Agent activation, plugin service responsiveness, application health, credential validity or mandatory journal availability. Sequential engine observations are not an atomic engine snapshot and can become stale after the query. Consumers should use the timestamp and underlying conditions when deciding their next explicit action.

The earlier cancellation-entry timeout remains undiagnosed; the merged [diagnostic increment](2026-09-30-cancellation-entry-diagnostics.md) remains in place. Implementing this separate query does not claim to fix that timeout.

## Verification scope

The HTTP regression first failed because the pre-change response had no `readiness` field. Tests exercise the shared query's healthy, blocked and incomplete paths, lifecycle and runtime ownership, in-process provisioning, preserved reconciliation, authority denial and cancellation after the final probe. Real Host/Orleans tests verify HTTP wire enums, authorization, parity with Grain queries, readiness after explicit container recovery and readiness after stopping an in-process workspace. The explicit Podman integration exercises readiness with an actual container and network before stopping, after recovery and after network detachment, including retained reconciliation. It substitutes authorization and the journal; those boundaries are verified separately by the Host tests. The PR records the tested commit, exact commands, actual outcomes and prerequisite skips.
