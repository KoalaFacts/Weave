# Installed MCP tool restoration during workspace reconciliation

## Supported path

An installed MCP connector can be registered on host startup while its tool actors still have no live handles. This increment extends [runtime reconciliation](2026-09-30-workspace-runtime-reconciliation.md) to workspaces whose hosted services consist of installed loopback HTTP MCP tools. It uses the existing persisted installation configuration, pinned operation contract and tool registry definitions.

1. Preserve the configured actor database and management journal when restarting the host.
2. The existing installation startup restorer registers eligible enabled MCP installations. Reconciliation requires the currently registered connector to match the retained installation target and pinned contract. A failed startup registration stays blocked; explicitly enable the installation to retry its connection before reconciliation.
3. Query `GET /api/workspaces/{workspaceId}/runtime` with `workspace:runtime:read`. Inspect `hostedServicePlan.mcpInstallations` for the exact endpoints, server identities, pinned operations and contracts, plus `requiredGrants`. This describes supported configuration, not current service health. The digest includes installation identity, definition revision, validated configuration digest, pinned contract and every retained tool definition's connection settings.
4. Submit both observed digests to `POST /api/workspaces/{workspaceId}/runtime/reconcile`, with a fresh `X-Weave-Management-Id`:

```json
{
  "expectedResourceSetDigest": "<resourceSetDigest from the observation>",
  "expectedHostedServiceDigest": "<hostedServicePlan.digest from the observation>"
}
```

5. The caller needs `workspace:runtime:reconcile`, `plugin:invoke:<workspaceId>/<pluginName>` for each enabled installation, and `tool:<toolName>:connect` for each tool. The shared operation validates current authority and durably admits the exact grants and combined plan digest before restoring connections.
6. Reconciliation freshly probes each pinned peer, reconnects tools from their owned registry definitions, probes the peers again and reobserves retained runtime resources. It rechecks the service plan and current grants before persisting confirmation. `Confirmed` with `hostedServicesRestored: true` is evidence of this completed operation.

No tool business operation is invoked. Tool discovery, installation registration, connection permission and restoration add no invocation grants. Saved tokens are not reused; the current request capability is passed through each connection boundary.

## Eligibility and blocking

All enabled installations must have supported authority state, the current definition revision, valid configuration digests, nonempty server/operation identity and a pinned SHA-256 contract. Targets remain the supported `http://127.0.0.1:<port>/mcp` profile. Tool names must match the pinned operations; tool URLs and installation names must match exactly. Unsupported tool types, stdio/server arguments, environment entries, extra plugin names, missing/duplicate definitions and disabled required installations block confirmation.

Active Agents and enabled Dapr installations still block with `hosted-services-require-restoration`. Heartbeats and Agent activation are not restored. Generic plugins, other tool families and credentials are not inferred from persisted names.

Missing connection grants return 403 before admission or reconnection. A changed service digest returns `PlanChanged`; missing or malformed required service digests return `InvalidRequest`. Missing current installation connections, unavailable peers and rejected contracts return `Blocked` with an explicit reason. Completion journal failure never reports success.

## Preservation, deliberate changes and limits

- Installation IDs, desired state, pinned contracts, Agent grants, retained resource identities, actor keys and persisted enum/field IDs are preserved. No provisioning, invocation replay, automatic approval consumption or external asset deletion is added.
- Resource-only requests retain their existing body. MCP restoration additionally requires the service digest. Unsupported workspaces stay blocked; there is no fallback restoration provider.
- `IToolActor.ConnectAsync` now accepts an explicit cancellation argument across Orleans RPC. Local capability cancellation is linked to that argument. Replacing a connection authorizes before disconnecting its old handle, then reauthorizes before connecting. This is a deliberate pre-1.0 wire operation change; cooperating hosts/clients must update together, with no alternate legacy method.
- `Readiness` still means runtime resource readiness. `hostedServicePlan` is configuration eligibility. `hostedServicesRestored` confirms one restoration operation; it is not a continuing application-health guarantee. Peers or containers can change after observations. No execution path treats reconciliation as an invocation grant.
- Workspace ownership serializes its operations; the tool registry owns restoration of its retained definitions. Cross-actor observations and runtime probes are not a distributed transaction or lease. A changed plan prevents confirmation when observed, and subsequent invocation still validates current installation/contract and authority.
- Restoring several tools can leave some connected if later work fails or is cancelled. The workspace stays blocked; no rollback of remote effects or automatic retry is claimed. Cancellation, revocation after restoration begins, or exceptions with uncertain completion retain unknown admission. Inspect the state and deliberately obtain a fresh plan and management ID; do not reuse an admitted ID to replay work.
- State confirmation and journal completion remain separate durable writes. Host restart again requires reconciliation; previous success does not certify the next host.
- In-process lifecycle hooks remain trusted code. The built-in restoration path performs metadata/connection requests without `tools/call`; it does not sandbox arbitrary hook contributions or guarantee that third-party hooks have no effects.

## Verification scope

The regression first failed with `hosted-services-require-restoration` after a real host restart and an empty tool handle. The integration path uses preserved SQLite actor and journal files plus an actual loopback MCP peer, exercises both maintained protocol versions, checks permission denial and a changed contract, reconnects without `tools/call`, verifies duplicate admission, and reads the retained confirmation/journal after another host restart. It does not require a model provider or container engine.

Focused tests cover invalid installation bindings, frozen installation/tool digests, fresh probe failure/disable, mandatory admission, plan changes, resource changes during restoration, revocation, cancellation and replacement handle cleanup. A real Orleans host test verifies explicit connection cancellation reaches a gated lifecycle hook. The gate is substituted; it is not proof about arbitrary third-party lifecycle hooks or network isolation. Full verification results belong in the PR for the tested commit.
