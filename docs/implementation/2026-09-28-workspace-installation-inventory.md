# Workspace installation inventory

`GET /api/plugins/installations/{workspaceId}` reads the stored MCP and Dapr
tool plugin installations for one workspace. It requires an
`X-Weave-Capability` token scoped to that workspace with the exact grant
`plugin:installations:read`, including when global API authentication is
disabled. A credential profile may issue this grant to an operator. A caller
must already know the workspace ID; the active-workspace list is not an
inventory of stopped workspaces.

Each entry contains the installation ID, plugin name, type, persisted
`desiredEnabled` intent, and `runtimeConnected`. The latter is a snapshot of
the current Host's plugin registry, not a peer health check or a promise that
the next invocation will succeed. The response omits endpoint URLs, ports,
configuration, schema digests, credentials, and invocation history. Unknown
workspaces return 404; inconsistent stored identities return 409 instead of
showing records under another workspace ID.

The inventory also reports the current Host's last installation observation:
`condition`, a safe `reasonCode`, and `lastCheckedAt` in UTC. `ready` means an
enabled installation has an active registration; `blocked` means an enabled
installation's most recent activation failed; `unobserved` means it is enabled
but has no activation result in this Host; `disabled` means the persisted
intent is off and no registration is active. `inconsistent` flags a remaining
active registration after the persisted intent was turned off. A ready or
disabled installation may retain a reason code from a failed activation
attempt while its current registration or disabled intent remains intact.
Intentional disable clears that reason. Reasons distinguish invalid stored
configuration, an unpinned or rejected MCP contract, invalid configuration,
registration conflicts, unsupported plugin types, unavailable MCP peers, and
other connection failures. Neither a reason code nor the timestamp includes
the peer's error text.

Observations are kept in memory for this Host, then rebuilt as restoration
attempts run after restart. An observation is not durable history or a
continuously refreshed health measurement. In particular, a registered Dapr
connector does not prove that its sidecar is reachable. No automatic retry or
external effect follows from reading this inventory.

Stopping a workspace disables and disconnects its installations while keeping
their records. With durable actor storage, an authorized query still returns
those records after a Host restart. Reading an unknown workspace no longer
creates a persisted actor identity: the ID is assigned when a workspace
starts and is written with its state. Existing stored workspace identities
and Orleans field IDs are unchanged.

This is a workspace-scoped read path, not a global installation catalogue,
uninstall operation, readiness probe, Tenant model, or reconciliation loop.
The active-workspace registry and its restoration behavior remain separate
from this persisted inventory.
