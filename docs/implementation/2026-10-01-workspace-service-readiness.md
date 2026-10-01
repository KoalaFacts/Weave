# Workspace readiness includes current service health

## Operator path

An operator reads `GET /api/workspaces/{workspaceId}/runtime` with `workspace:runtime:read` for that workspace. The response's `readiness` now combines retained resource observations, current host confirmation and the existing hosted-service observation. The same workspace operation supplies Orleans callers and HTTP ingress.

Previously, healthy containers/network could produce `readiness.condition: Ready` alongside a service diagnostic reporting a disconnected tool or rejected MCP contract. This increment deliberately replaces that resource-only aggregation. The existing per-resource and per-installation diagnostics remain available to explain the result.

| Resource readiness | Service observation | Workspace readiness |
| --- | --- | --- |
| `Ready` | No hosted services, or `Ready` | `Ready` |
| Any | `NotReady` | `NotReady`, with `HostedServicesNotReady` |
| `NotReady` | `Ready`, `Unknown` or an unrecognized service condition | `NotReady`; resource reasons remain |
| `Ready` or `Unknown` | `Unknown` or an unrecognized service condition | `Unknown`, with `HostedServiceObservationIncomplete` |
| `Unknown` | No hosted services, or `Ready` | `Unknown`; resource reasons remain |

Unsupported Agent, Dapr and other service profiles retain their existing `Unknown` service diagnostic. They now also prevent overall `Ready`. Healthy supported services do not erase resource failures or incomplete observations. Resource-only workspaces preserve their existing readiness behavior and omit the service observation.

The final observation returned after supported MCP restoration includes the same service reasons if verification blocks confirmation. A successful confirmation still requires healthy resources and services under the existing frozen plans and current grants.

## Current health and historical recovery

`ConfirmedOnCurrentHost` and the recovery condition describe recorded lifecycle confirmation. They remain true/confirmed when a later read observes a broken service. Current `readiness` can become `NotReady` without rewriting that historical evidence. Reads do not reconnect tools, create management journal entries, modify desired installation state or invoke business operations.

The existing [explicit recovery path](2026-09-30-workspace-recovery-verification.md) is preserved. Operators inspect diagnostics and submit a fresh frozen plan and management ID when another recovery is needed. No polling controller, automatic retry or invocation replay is introduced.

## Contract and limits

There is no new endpoint, grant, dependency or snapshot property. `HostedServicesNotReady` and `HostedServiceObservationIncomplete` are appended to the reason enum; existing numeric values and all persisted field IDs, actor keys and resource/service digests are preserved. Consumers must accept the two added reason strings and the stricter meaning of top-level readiness. No second resource-only readiness field or compatibility path is added.

Observations remain sequential request-time checks. They can become stale and do not provide a health lease, atomic cross-actor snapshot, successful future invocation or isolation guarantee. This increment adds no new service restoration profile or production container evidence.

## Verification scope

Regression checks first demonstrated the missing aggregation against the old implementation. Unit cases exercise the resource/service condition combinations, unrecognized service values, unsupported profiles, preservation of resource reasons and state, absence of management effects, current authorization and cancellation.

Real HTTP MCP and Orleans checks cover both maintained protocol revisions, healthy/changed-contract/healthy-again/disconnected/unavailable observations, and disconnection after successful recovery across preserved SQLite files. They verify current readiness, HTTP/Orleans agreement, retained confirmation and journal evidence, no implicit reconnection and zero business invocations. The container MCP recovery fixture now explicitly checks healthy resources alongside an unsupported service profile and overall `Unknown`.

An initial full run also encountered an external Python TCP client's nonzero exit without its captured stderr being reported. Its existing stderr assertion now runs before the exit-code assertion; timeouts, process cleanup and read/write authorization checks are preserved. The focused case passed, but the original intermittent exit's cause remains unconfirmed. This diagnostic change is not a claim that the original failure is fixed. Full-suite results and prerequisite skips belong to the PR for its tested commit. Repository checklists are self-review, not independent review.

A subsequent full run exposed a Windows cancellation-fixture race: `File.Exists` observed the PID file while PowerShell still held its write handle, causing a sharing violation during the read. The child now closes a separate pending file before publishing the PID by rename in the same directory. The test observes the runner's completion before deleting both files, preserves its deadlines and cancellation/actual-child-exit assertions, and passed ten focused repetitions. Production process execution is unchanged.

Another full run encountered the previously intermittent HTTP cancellation authorization-entry timeout. Test-local observers now distinguish middleware entry, request-body serialization, Grain resolution and authorizer entry. Positive checks exercise each observer through the real HTTP/Orleans path; all original deadlines, cancellation propagation and no-effect assertions remain. No production routing, test parallelism setting or retry has changed. A passing repetition does not establish the cause of the intermittent timeout or prove it fixed.
