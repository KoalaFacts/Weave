# Explicit workspace runtime reconciliation

## Complete supported path

A preserved workspace can outlive the host that provisioned its resources. Container recovery alone leaves `RequiresReconciliation`. This increment adds an explicit way to confirm a **resource-only workspace** after checking its exact retained network, container IDs and attachments.

The following [MCP restoration increment](2026-09-30-workspace-mcp-restoration.md) also supports installed HTTP MCP tools, with a separate frozen service digest and current connection grants. The resource-only path below remains supported; Agent and other service restoration remains blocked.

1. Query `GET /api/workspaces/{workspaceId}/runtime` with `workspace:runtime:read`.
2. If a retained container is stopped, use the existing container recovery operation with its separate `workspace:runtime:recover` grant. Missing resources are not recreated.
3. Submit the observed `resourceSetDigest` to `POST /api/workspaces/{workspaceId}/runtime/reconcile`, using `workspace:runtime:reconcile` and a fresh `X-Weave-Management-Id`.
4. The shared actor operation records mandatory admission, probes resources, revalidates current authority and the resource set, and persists confirmation. Subsequent runtime observations can report `Ready`.

Request body:

```json
{
  "expectedResourceSetDigest": "<resourceSetDigest from the runtime observation>"
}
```

No token is retained for later dispatch. The reconciliation grant allows inspection for this operation; it does not confer tool invocation, plugin installation or container recovery authority. HTTP and Orleans use the same owner-bound operation.

## Eligibility and results

The workspace must be registered as `Running`, use the current runtime kind, have a known recovery condition and unambiguous retained container identities. A network must be observed as `Present`, or `NotRequired` with no containers. Every retained container must be `Running` and attached to the retained network. Unknown or unsupported observations cannot confirm readiness.

Active Agents, enabled Dapr installations and unsupported tool/plugin configurations block with `hosted-services-require-restoration`. Supported installed HTTP MCP tools follow the linked restoration path. Reconciliation does not clear unsupported service recovery requirements merely because their containers look healthy.

| Outcome | Meaning | HTTP status |
| --- | --- | --- |
| `Confirmed` | Resource observations and the local confirmation write succeeded; completion evidence was recorded. | 200 |
| `Blocked` | Lifecycle, identities, hosted services or resource observations prevent confirmation; see `reason` and optional `observation`. | 409 |
| `PlanChanged` | The observed resource set no longer matches the request. Obtain a fresh observation and explicitly reevaluate. | 409 |
| `InvalidRequest` | The digest or management ID is invalid. | 400 |
| `AlreadyAdmitted` | This canonical management ID was retained; no repeat operation is performed. | 409 |
| `OutcomeUnknown` | The state write did not return a confirmed result; admission remains unknown. | 409 |
| `EvidenceUnconfirmed` | Completion recording failed; success is not reported. | 500 |

Authorization failures remain 401/403 at HTTP ingress and denied at the actor boundary. A missing workspace returns 404. The HTTP operation has a 30-second deadline; expiration before confirmation returns 504 with `runtime-reconciliation-unconfirmed`. Once a confirmation write starts, it is awaited with a separate five-second service-owned deadline, so caller cancellation cannot imply that no local write occurred.

## Preserved and changed semantics

- `RuntimeReconciledOnThisHost` is appended to `WorkspaceRecoveryCondition`; existing enum values, actor keys and persisted field IDs remain unchanged. Confirmation updates only the existing runtime instance ID and recovery condition. Resource IDs, registration status, start time, installations and history remain intact.
- `confirmedOnCurrentHost` distinguishes confirmed runtime ownership from provisioning. `startedOnCurrentHost` remains false for reconciled workspaces. Both forms can satisfy **runtime resource readiness**.
- `resourceSetDigest` uses SHA-256 over a versioned binary encoding. Strings are UTF-8 with BinaryWriter length prefixes; enum values and collection counts are integers. It includes workspace ID, lifecycle and recovery condition, stored runtime instance/kind, network ID, sorted retained container IDs, sorted active service names and sorted enabled installation IDs. Ordering does not change the plan; multiplicity does. It is a target/version precondition, not a lease or a complete deployment manifest.
- Mandatory management admission precedes probes and state mutation. Current authority and the digest are checked again immediately before confirmation. Reusing an admitted ID never retries a write, including after an unknown result.
- A new host activation detects the changed runtime instance and marks even a previously reconciled workspace as `RequiresReconciliation` again.

## Limits

The resource-only operation observes and confirms retained resources. It does not create or start resources, activate Agent services, replay invocations, change grants or restore credentials. Supported MCP tool reconnection requires the additional restoration path. It does not establish continuing application health or mandatory journal availability. In-process `local` workspaces are supported too.

The workspace actor serializes its operations on one owner. Resource probes are sequential and can become stale after observation; this is not an engine transaction, lease, cluster-wide adoption protocol or fencing mechanism.

The actor store and management journal are separate stores. Admission is durable before the write, but confirmation and journal completion are not atomic together. A failed state write restores the conservative live view and leaves admission unknown; a provider might already have committed. A completion failure can leave a confirmed workspace record while returning `EvidenceUnconfirmed`. Inspect current state and retained management evidence; neither result is permission to replay external effects. Memory actor storage cannot preserve confirmation through a host restart; restart recovery requires preserved actor storage.

## Verification

The HTTP regression failed on the old implementation with 404 for the missing reconciliation route. Unit tests exercise healthy confirmation, blocked and unknown dependencies, hosted-service exclusion, plan changes during observation, revocation before commit, recording failure, write uncertainty and cancellation. A real Host/Orleans test uses preserved SQLite actor and journal files across three host instances to verify confirmation persistence, fresh reconciliation after another restart, HTTP authorization and duplicate/stale request behavior. Two concurrent submissions of the same frozen resource set produce one confirmation and one rejected stale plan.

A separate explicit Podman integration scenario uses an actual network and container with a simulated previous runtime instance. It verifies that reconciliation reaches runtime readiness while preserving the original engine IDs. Its authorization, journal and state-write callback are substituted; the SQLite and authority boundaries are covered by the Host test. The PR records actual commands, tested commit, outcomes and skipped prerequisites. These are self-review and regression evidence, not an independent architecture or security audit.
