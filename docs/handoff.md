# Handoff — Capability-Bound Audit Log

> **Live state only.** The contract — principles, vocabulary table, roadmap — lives in [unique-agent-strategy.md](unique-agent-strategy.md). This file records the current shape of the system and what to pick up next.
>
> Update at the end of every session that advances the roadmap.

## Current state

The capability vocabulary is 6 verbs, all enforced through one shared authorizer:

| Verb | Manifest declaration | Runtime enforcement |
|---|---|---|
| `tool:<name>` / `tool:*` | yes | `CapabilityAuthorizer` from `src/Tools/Weave.Tools/Actors/ToolActor.cs` |
| `secret:<path>` | yes | `src/Security/Weave.Security/Actors/SecretProxyActor.cs` (still inline — see follow-ups) |
| `skill:read` / `skill:write` | yes | `CapabilityAuthorizer` from `src/Assistants/Weave.Agents/Actors/SkillMemoryActor.cs` |
| `channel:send:<id>` / `channel:receive:<id>` | yes | `CapabilityAuthorizer` from `src/Assistants/Weave.Agents/Actors/ChannelGatewayActor.cs` |
| `user:read:<userId>` / `user:write:<userId>` | yes | `CapabilityAuthorizer` from `src/Assistants/Weave.Agents/Actors/UserModelActor.cs` |
| `plugin:invoke:<plugin>` | yes | `CapabilityAuthorizer` from `src/Runtime/Weave.Silo/Plugins/PluginRegistry.cs` |
| `marketplace:install` | not implemented — see Next work | — |

Tests: 1807 passed.

### How a verb is wired today

Five sites that previously held a private `Authorize(...)` method now share `ICapabilityAuthorizer` ([CapabilityAuthorizer.cs](../src/Security/Weave.Security/Tokens/CapabilityAuthorizer.cs)). The authorizer:

1. **Validates** signature, expiry, revocation via `ICapabilityTokenService.Validate`.
2. **Workspace-matches** when `actorWorkspaceId` is non-empty (PluginRegistry passes `null` since plugins are silo-wide).
3. **Grant-checks** via `CapabilityToken.HasGrant` (segment-wise wildcards).
4. **Publishes** `CapabilityAuthorizationEvent` ([CapabilityAuthorizationEvent.cs](../src/Security/Weave.Security/Events/CapabilityAuthorizationEvent.cs)) on every call (allow OR deny) keyed by `tokenId`, `grant`, `workspaceId`, `issuedTo`, `outcome`, `actionContext`, and `reason` on denies (`"invalid-or-expired-token"` / `"workspace-mismatch"` / `"grant-missing"`).
5. **Throws** `UnauthorizedAccessException` on deny.

Call sites pass `actionContext` as a literal string (e.g. `"ChannelGatewayActor.RouteInbound:receive"`) or default to `[CallerMemberName]`.

DI registration lives in `src/Runtime/Weave.Silo/Startup/SiloServiceRegistrar.cs` `RegisterSecurity()`. Per-request token minting at the API boundary, manifest-gated runtime mints, and cancellation propagation are unchanged.

## Next work

The natural next move is **roadmap #3 — Capability replay/debugger**. The audit row from #2 is the source of truth: given a `tokenId` (or `workspaceId`/timeframe) it surfaces every action that token authorized, with the deny/allow trace. No additional plumbing is required at the authorizer side — `CapabilityAuthorizationEvent` already carries `tokenId`, `grant`, `outcome`, `reason`, `actionContext`, and `timestamp`. What's missing:

- A subscriber that materializes events into a queryable store (durable or in-memory). The strategy doc's *Audit completeness* metric (100% of denies produce a row with capability, grant, actor, reason) is the acceptance bar.
- A read API or CLI surface that takes a `tokenId` and prints the chronological row stream.
- An optional Blazor view in the dashboard for the same query.

**Don't pursue `marketplace:install` yet** — `IMarketplaceActor.IncrementInstallCountAsync` is a counter, not an install path. Gating an action that doesn't exist is empty ceremony. Wait until someone wires real marketplace-to-workspace installation, then gate it through the same authorizer.

## Cross-cutting follow-ups

These remain real gaps. None blocks #3.

- **`secret:<path>` enforcement is still inline in `VaultSecretProvider.cs:17-23`** — `Validate` + `HasGrant` with no workspace gate, no `LogWarning`. It's called from outside an Orleans actor (HTTP path). Wiring it through `CapabilityAuthorizer` needs a workspace-mismatch decision (Vault scopes mounts by `token.WorkspaceId`, so cross-workspace is silently impossible today). Cheapest fix: pass `actorWorkspaceId: token.WorkspaceId` so the workspace branch is a no-op until a real cross-workspace surface appears.
- **`ToolRegistryConnector` self-mints `[$"tool:{toolName}", "secret:*"]`** without consulting the agent's manifest. The connector is workspace-scoped (no `AgentDefinition` in scope), so the manifest-gate fix is less obvious. Likely shape: pass the requesting agent's capabilities through, or treat tool registration as a workspace-admin verb gated by a separate grant.
- **`channel:send:*` requires both grants today** because `RouteInboundAsync` does both ingress and reply atomically. The double `Authorize` call now lives at the call site (lines 74–75 of ChannelGatewayActor) with distinct `actionContext` strings (`":receive"` / `":send"`), so the audit log distinguishes them. If a webhook adapter ever needs receive-only, split the actor surface.
- **Manifest-side wildcards.** Runtime mint sites use literal `.Contains(grant)` against `state.Definition.Capabilities`. A manifest declaring `skill:*` doesn't grant `skill:read`/`skill:write` for the runtime gate — the manifest check ignores wildcards. Wire `CapabilityToken`-style segment matching into the manifest check when needed.

## Template to mirror

For roadmap #3 (replay/debugger): subscribe to `CapabilityAuthorizationEvent` from a singleton service registered next to the authorizer. The event already carries everything needed.

| What | Where |
|---|---|
| Single authorizer with Authorize signature | `src/Security/Weave.Security/Tokens/CapabilityAuthorizer.cs` |
| Audit event record | `src/Security/Weave.Security/Events/CapabilityAuthorizationEvent.cs` |
| Per-actor call-site shape | `await authorizer.AuthorizeAsync(token, grant, actorWorkspaceId, actionContext);` |
| DI seam | `SiloServiceRegistrar.RegisterSecurity()` |
| Authorizer unit test scaffold | `src/Security/Weave.Security.Tests/CapabilityAuthorizerTests.cs` |
| Per-actor audit-event smoke test | e.g. `RouteInboundAsync_OnDeniedReceive_PublishesEventWithChannelReceiveGrant` in `ChannelGatewayActorTests.cs` |

## History

- **2026-05-03** (`claude/competitor-analysis-handoff-Qn9jh`) — capability-bound audit log shipped; five `Authorize` copies consolidated into `ICapabilityAuthorizer`; allow + deny rows publish `CapabilityAuthorizationEvent` keyed by tokenId/grant/workspaceId/issuedTo/outcome/reason/actionContext; tests 1790 → 1807
- **2026-05-03** (`claude/continue-agent-strategy-yS2Z1`) — `channel:*`, `user:*`, `plugin:invoke:*` shipped; capability cancellation linkage; mid-segment wildcards; `IReadOnlyList<T>` manifest cleanup; four PR #40 follow-ups; plugin types moved Workspaces → Silo to enable the verb
- **2026-05-03** — `skill:read` / `skill:write` shipped (PR #40)
- **2026-05-03** — Strategy doc + best-practices audit (PR #39)
