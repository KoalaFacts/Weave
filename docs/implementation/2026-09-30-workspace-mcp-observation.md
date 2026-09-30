# Current MCP service observation

## Complete path

An installation record and a successful earlier reconciliation do not prove that a tool is still connected or that its peer still serves the pinned contract. `GET /api/workspaces/{workspaceId}/runtime` now adds `hostedServiceObservation` for workspaces with hosted services, alongside the existing resource `readiness` and frozen `hostedServicePlan`.

The caller needs `workspace:runtime:read` for the actual workspace. The shared workspace operation checks current authority before observation and again before returning the result. The existing HTTP ten-second observation deadline and caller cancellation apply to the peer probes and Orleans connection queries.

For supported installed loopback HTTP MCP tools, each read:

1. Validates the retained installation/tool bindings and frozen service digest.
2. Requires an active registered connector matching the exact installation configuration and pinned contract before contacting its peer.
3. Opens a temporary metadata probe against that pinned peer. This checks server identity, operation and contract; it sends no `tools/call`.
4. Queries the tool actor for a live handle attached to the current connector of the expected type and installation ID. A stale handle or a connection to a different installation is insufficient.
5. Checks registration again after probing and revalidates the retained service plan. Changed plans produce `Unknown`, never a success inferred from an earlier configuration.

Example diagnostic fragment:

```json
{
  "hostedServiceObservation": {
    "condition": "NotReady",
    "reason": null,
    "mcpInstallations": [
      {
        "installationId": "example/echo_server",
        "toolName": "echo",
        "condition": "NotReady",
        "reason": "mcp-peer-unavailable"
      }
    ]
  }
}
```

## Meaning and limits

| Condition / reason | Meaning |
| --- | --- |
| `Ready` | The supported peer's metadata contract matched and its tool had a current connection to the expected installation when checked. |
| `NotReady` / `mcp-installation-not-restored` | The current installation connection is missing, disabled or does not match the retained target/contract. No peer probe is sent for an initially unmatched installation. |
| `NotReady` / `mcp-tool-not-connected` | The peer matched, but the expected tool has no current connection. |
| `NotReady` / `mcp-tool-not-recorded` | The installation's operation has no active tool contribution. |
| `NotReady` / `mcp-peer-unavailable` | The peer could not be reached or returned an unavailable transport result. |
| `NotReady` / `mcp-contract-rejected` | Fresh peer metadata no longer matches the pinned contract. |
| `Unknown` / `mcp-observation-incomplete` | The probe could not establish a complete supported observation. |
| `Unknown` / `hosted-service-plan-changed` | Retained configuration changed during the observation or did not match the supplied internal digest. |

For multiple installations, any `NotReady` result prevents aggregate readiness; otherwise an `Unknown` result prevents it. A healthy peer does not hide an unavailable or unconfirmed peer. Unsupported Agent, Dapr or other service profiles remain `Unknown` with `hosted-services-require-restoration`; they are not probed. Resource-only workspaces omit the service observation.

The resource `readiness` contract remains unchanged. It describes retained containers/network and host confirmation; it can be `Ready` while MCP services are `NotReady`. `hostedServicePlan` describes recovery eligibility, and can stay unchanged while a peer goes offline or changes its contract. Service observation is a request-time diagnostic, not a background monitor, lease, execution grant or sandbox. Observations are sequential and may become stale; they do not prove that every changed connection setting has been reapplied or that a subsequent business operation will succeed.

Reads do not reconnect retained handles, admit management operations, change installation desired state, persist a new recovery confirmation or replay invocations. Temporary probe transports are disposed. A failed service observation does not rewrite earlier confirmation/evidence; operators can inspect and deliberately use the existing explicit recovery path.

[Explicit reconciliation now verifies the same current service health after reconnection](2026-09-30-workspace-recovery-verification.md). A non-ready service observation prevents a new recovery confirmation and is returned with its installation diagnostics.

Persisted field IDs, actor keys and enum values are unchanged. The tool actor gains an owned current-connection query; it does not expose configuration, credentials or a connector instance. Runtime JSON gains the optional service observation; cooperating hosts must include the updated Orleans operation and serializers.

## Verification scope

The regression first failed at the real HTTP boundary because the runtime response lacked service diagnostics, for both maintained protocol versions. The completed integration path uses a real loopback MCP peer and Host/Orleans, verifies authorized and denied reads, observes healthy, contract-changed, healthy-again, disconnected and stopped-peer states, preserves the retained plan and connection identity, and asserts zero business calls. Unit checks exercise exact connection type/installation matching, stale connector replacement/removal, cancellation, revocation during observation, changed plans, disabled registration, mixed peer outcomes and incomplete probes. This is not continuous health monitoring, network isolation or live container verification.
