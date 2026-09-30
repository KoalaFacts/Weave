# Workspace network observation and dependency-aware recovery

## Delivered path

The existing `GET /api/workspaces/{workspaceId}/runtime` snapshot now includes `network.networkId`, `network.condition`, and each container's `networkAttachment`. A running container alone does not establish its workspace network dependency. The existing `workspace:runtime:read` grant remains required; observations do not mutate retained workspace state.

Network conditions distinguish `Present`, `Missing`, `InvalidIdentity`, `Unavailable`, `Unknown`, `Unsupported`, `RuntimeMismatch` and `NotRecorded`. Container attachments distinguish `Attached`, `Detached`, `Unknown`, `Unavailable`, `Unsupported`, `InvalidIdentity` and `NotChecked`. An unavailable or absent network leaves attachment `NotChecked`; it is not proof that a container is detached.

The verified Podman engine reports an empty attachment after disconnecting a running container, but a named `bridge` placeholder after stopping it. The former reports `Detached`; the latter reports `Unknown` and blocks recovery. A provider-reported name is not promoted to an immutable binding.

The existing `POST /api/workspaces/{workspaceId}/containers/{containerId}/recover` route and `workspace:runtime:recover` grant remain. Its result now includes the network observation and attachment condition. HTTP and Orleans use the same recovery component, mandatory management admission and duplicate protection.

## Recovery boundaries

- Recovery requires a retained network ID. Both network and container identifiers must be full lowercase 64-character engine IDs. Names and abbreviated IDs do not select a recovery target.
- A stopped or already-running container must be attached to the exact retained network. Missing, unconfirmed or detached dependencies return `Blocked` without starting or reconnecting anything.
- A stopped-container recovery checks the dependency again before dispatch, then revalidates current capability authority immediately before start. After start it verifies both running state and network attachment. Unconfirmed post-dispatch dependencies leave `OutcomeUnknown`; an engine effect is not treated as absent.
- Management admission binds its request digest to the retained network ID as well as the workspace/container target. Existing management IDs remain retained and cannot be reused to dispatch again.
- Selected CLI projections contain engine IDs and state, with bounded parsing. Network inspection does not capture container environment, secrets, IP addresses or unrelated inspection payloads. Provider failures retain typed diagnostic conditions and log error types without raw stderr.
- These are fresh observations, not an atomic transaction with the engine. An external operator can change a network after a check. Network attachment does not prove application connectivity, Agent readiness or complete workspace reconciliation.

## Provisioning and retained state

Podman `network create` returns a name. New provisioning now inspects that newly created network once to obtain its immutable ID. Docker's returned ID is validated directly. Unconfirmed creation identity fails rather than storing an ambiguous binding. Creation and inspection are not transactional; a failed confirmation can leave an external resource requiring operator inspection.

Old persisted Podman network names are preserved. Observation reports `InvalidIdentity`, and recovery is blocked; there is no name-based recovery fallback or automatic reassociation. A trusted migration to verified immutable network bindings remains future work. No actor keys, persisted field IDs, installation history or management records are reset.

Podman network removal uses its supported plain `network rm` operation. When removal fails, only an exact-ID observation confirming absence makes repeated cleanup successful. An existing, unavailable or unconfirmed network retains the failure. Removal does not request Podman's force option, which can affect attached containers.

The runtime recovery contract now requires an explicit network ID. Both runtime implementations and direct callers are updated; in-process runtime observations and recovery report `Unsupported`.

## Verification

The Podman creation regression first reproduced a retained network name where an immutable ID was expected. Unit tests cover missing and detached networks, malformed/ambiguous projections, full-ID enforcement, recovery admission, lost dependencies before/after dispatch, cancellation and failed cleanup confirmation.

HTTP/Orleans tests use the real Host and SQLite management journal with a scripted engine. They verify the snapshot, blocked recovery, recorded failure and absence of CLI start. The explicit Podman integration creates a temporary network/container, verifies same-ID recovery, disconnects the container, verifies recovery is blocked, removes the network and verifies the missing dependency is blocked. Cleanup affects only its temporary resources. This test is tagged `Category=Integration` and excluded from the default workspace test invocation.

Docker command-shape coverage does not establish Docker engine integration. The implementation does not provision a replacement network, reconnect a container, resume Agent work or clear `RequiresReconciliation`.
