# Workspace runtime observation and explicit container recovery

This increment is extended by [network observation and dependency-aware recovery](2026-09-30-workspace-network-recovery.md), which adds retained-network and attachment requirements to the existing route.

## Delivered path

Weave can query the container engine for each retained container ID, then explicitly start one stopped container. This is the first workspace runtime recovery path in the Agent Control Plane. It does not restore a complete workspace after a Host restart.

| HTTP operation | Required workspace capability | Result |
| --- | --- | --- |
| `GET /api/workspaces/{workspaceId}/runtime` | `workspace:runtime:read` | A fresh snapshot of registration, creating/current runtime, Host ownership and container observations. |
| `POST /api/workspaces/{workspaceId}/containers/{containerId}/recover` | `workspace:runtime:recover` | `Started`, `AlreadyRunning`, `Blocked` or `OutcomeUnknown` for one retained ID. Duplicate IDs return `AlreadyAdmitted`; unconfirmed evidence returns `EvidenceUnconfirmed`. No request body is required. |
| `GET /api/management/operations/{workspaceId}/{id}` | `management:operations:read` | Retained metadata for the management operation. |

Use the existing signed `X-Weave-Capability` header. Recovery accepts an optional `X-Weave-Management-Id` containing a nonempty UUID in 32-character `N` format; otherwise the Host generates one and returns it in that header. Keep this ID when a response is lost. Reusing an admitted ID returns a conflict without dispatching another start.

Observation queries are on demand. `observedAt` identifies completion of the snapshot; observations are not a durable history or a scheduled container controller. `registeredStatus` remains distinct from the engine's `condition`. A registered `Running` workspace can contain a stopped or missing container. `startedOnCurrentHost` concerns the retained creating runtime instance, not Agent or connector readiness.

Docker and Podman observations use a selected `container ls` format containing only the full ID and engine state. Weave rejects names, abbreviated IDs, ambiguous output and unknown states. A compatible creating runtime is required; an installation cannot redirect recovery to a different provider. In-process containers report `Unsupported` rather than simulated readiness.

## Recovery boundaries

- The Workspace actor serializes observation/recovery with its existing lifecycle operations. Recovery requires a registered running workspace, exactly one retained matching container ID, and the same creating runtime name.
- The actor uses the same runtime recovery component for HTTP and Orleans calls. That component authorizes the workspace and atomically admits the management ID before the effect path; both callers share duplicate protection. After the stopped-container observation, it checks current signature, expiry, revocation and grants again immediately before the CLI start.
- Recovery starts the exact retained container. It does not provision a replacement, resolve a name, pull another image, change installation intent, resume an Agent task or replay a tool invocation. Starting a container does run its entrypoint again; operators must account for that workload behavior.
- A running container returns `AlreadyRunning` without a start. Missing containers, transition states, invalid identities and unavailable runtimes block recovery. A successful CLI start requires a subsequent running observation to return `Started`.
- A failed or lost start response, unconfirmed post-start observation, or recovery deadline leaves the management outcome unknown. Inspect the engine and retained management record before considering a fresh operation. The Host makes no automatic retry.
- HTTP runtime operations have a ten-second deadline. Request cancellation propagates. Cancelling the local CLI process cannot undo an effect already accepted by the engine.
- Container recovery after a Host restart preserves `RequiresReconciliation` and the original runtime instance ID. A running container does not restore lost Agent state, tool registrations, credentials or authority. Network reconciliation and missing-resource replacement remain future work.

## Preserved and changed

Workspace actor keys, persisted state fields, network/container IDs, existing start/stop routes and installation history are preserved. Observation does not overwrite persisted container status. No stored-state reset or migration is required.

The two runtime implementations implement explicit observation/recovery operations; call sites use the updated interfaces directly. A recovery dispatch callback lets the control plane reauthorize after observation without putting capability validation into a container provider. The actor receives an owned runtime recovery component through constructor injection; that component shares the Host's mandatory journal. Orleans recovery calls supply their own management UUID. Actor methods delegate this responsibility rather than adding another lifecycle implementation to the existing actor.

The shared process runner now drains both streams concurrently while retaining at most 65,536 characters per stream, rejects excess output, and terminates its local process tree on cancellation. Nonzero-exit exceptions retain command and exit code but omit raw stderr, which can contain sensitive provider output.

## Verification

Tests cover full-ID routing, denial and lifecycle/provider mismatch, malformed observations, unknown outcomes, cancellation, current authority after observation, retained reconciliation status, real HTTP/Orleans dispatch and SQLite management admission, duplicate requests and concurrent recovery. Process tests exercise actual child-process cancellation and excess stdout/stderr.

`ContainerRuntimeRecoveryIntegrationTests` is tagged `Category=Integration` and excluded from the default workspace test invocation. Run it explicitly against a configured Podman engine. The extended test creates a temporary network/container, observes running/stopped state, starts the same ID, checks network dependency failures and missing-resource recovery, and cleans up its own resources.

These tests do not establish Docker integration, cluster-wide recovery, durable observation history or a complete Agent recovery controller.
