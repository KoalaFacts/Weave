# Management authority, workspace runtime identity, and audit decision

## Delivered boundaries

Workspace creation requires a `silo`-scoped capability with `workspace:create`.
Workspace stop requires a capability scoped to that workspace with
`workspace:stop`. These checks apply whether or not the manifest contains a
Dapr or MCP tool installation. Such installations still require their separate
install/disable grants. Plugin registration and removal outside an installation
require `plugin:connect` and `plugin:disconnect`, respectively, scoped to
`silo`. Read-only plugin inventory, composition, and the type catalogue remain
under the Host-level authentication boundary and do not confer execution
grants. Global API authentication and the optional trusted-operator gate
remain additional deployment boundaries.

The CLI's `workspace up`, `workspace down`, `run`, and import start paths
already accept `--capability-file`. They now require an appropriate token for
ordinary workspaces too; the default unauthenticated local workflow will
receive an authorization failure until an operator provisions that token.

The container runtime now receives the immutable workspace ID when provisioning.
Default resource names and `{workspace}` substitutions use that ID, not the
display name. Teardown uses the container and network IDs retained in workspace
state. This also covers a configured network name that differs from the default.
New starts also retain the creating runtime type (`in-process`, `docker`, or
`podman`). Stop refuses to hand retained IDs to a different runtime, before
agent or plugin cleanup begins. Older persisted states without that identity
need operator reconciliation before automated teardown; their IDs are not
discarded or redirected. No stored field is renumbered or reset.

Teardown retries confirmed missing resources safely and attempts every retained
container and network even if an earlier removal fails. A genuine or uncertain
failure retains the IDs and a recovery-required error state so a later stop can
retry on the matching runtime. Podman network removal does not use its
container-deleting force option. A successful stop clears the retained IDs.

A runtime instance ID accompanies a newly started workspace. When a durable
`Running` state is activated by a later Host instance, the workspace response
and the CLI/TUI report `RequiresReconciliation`. It does not silently claim
that containers, agents, tools, and external services have all resumed. The
condition is diagnostic:
restored external Dapr and MCP installations still use their independent
connection, contract, and invocation-authority checks. Installation `ready`
means registration only, not full workspace recovery.
The available operator recovery path is to inspect and stop the old workspace,
then create a new workspace from a reviewed manifest. This increment does not
implement automatic workspace reprovisioning or non-idempotent replay.

## Management audit decision

The existing capability audit stream is a bounded diagnostic trace. Its default
backend is memory, delivery is through a swappable in-process event bus, and a
subscriber can discard a row after retry exhaustion. Configuring SQLite or
PostgreSQL makes retained rows persistent but does not make delivery mandatory.
It must not be described as complete administrative evidence.

Before management writes can be advertised as durably governed, each action
must have a stable management-operation ID and a mandatory local admission
record committed **before** its effect. That record must bind the authenticated
subject, exact action and target/workspace, validated authority, request digest,
and time. A separate completion record must distinguish confirmed success,
confirmed denial/failure, and unknown outcome. A missing or failed mandatory
write blocks new effects; an uncertain external effect is not retried under a
fresh ID. Retained IDs and evidence require an explicit operator-controlled
retention policy, not silent FIFO eviction. Single-host deployments can use a
protected local SQLite journal; a multi-host claim requires a shared store and
concurrency/recovery evidence. The existing invocation journal remains the
authority for tool attempts and approvals and should not be confused with the
diagnostic capability trace.

This is a requirement decision, not a claim that a management-operation journal
or per-human administrative identity has been delivered. Other legacy
administrative routes still rely on the configured Host-level authentication or
trusted-operator gate and need an explicit operation-by-operation authority
migration before the entire Host is a unified management surface.
