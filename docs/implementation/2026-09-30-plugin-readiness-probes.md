# Continuous plugin installation readiness diagnostics

The Host schedules a pass over desired-enabled Dapr and MCP tool installations
in running workspaces on a 30-second timer. Passes run sequentially and can
take longer than one interval when many peers are slow. Each probe has a
five-second limit. Probing never invokes a tool, changes installation intent,
or reconnects a plugin. The first scheduled pass follows the first interval
after Host start;
startup restoration remains the separate activation path.

The authorized workspace installation inventory adds `probeCondition`
(`unobserved`, `responding`, `blocked`, or `not_applicable`), a safe
`probeReasonCode`, and `probeCheckedAt`. The existing `condition` and
`runtimeConnected` fields continue to describe runtime registration. A
responding peer with no registration tells an operator that the peer is back,
but does not promise that re-enabling will succeed. A blocked probe on an
active registration does not silently disable that registration or bypass the
normal pre-dispatch checks. The inventory still requires the exact
workspace-scoped `plugin:installations:read` capability.

For Dapr, the probe sends a GET to the configured loopback port's
`/v1.0/healthz/outbound` endpoint and accepts only HTTP 204. Redirects and
proxies are disabled for this client. This follows the
[Dapr health API](https://docs.dapr.io/reference/api/health_api/) for sidecar
outbound readiness. It does not verify the target app ID, sidecar identity, or
a subsequent service invocation. It is a passive infrastructure diagnostic,
not an application health dependency.

For MCP, a temporary connector initializes the pinned loopback endpoint and
checks the stored server identity, operation, protocol revision, and contract
digest. It closes the connection without calling the operation. A changed
contract is reported as `contract_rejected`; transport failures are reported
as `peer_unavailable`. The response never includes endpoint URLs, peer text,
schema contents, or configuration.

The monitor skips installations with unsupported authority, invalid identity,
changed definition revision, or invalid stored configuration. It re-reads the
workspace state after each probe and discards a result if the installation was
disabled or changed while the probe ran. Observations are kept only in this
Host's memory and are matched to the stored revision and digests. A restart
starts with `unobserved` until a new probe completes. This does not add
automatic activation, provisioning, reconciliation of external assets, or
replay of uncertain operations. Operators still use the existing authorized
management path to re-enable an installation after reviewing its state.
