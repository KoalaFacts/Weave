# Handoff — Capability Replay/Debugger

> **Live state only.** The contract — principles, vocabulary table, roadmap — lives in [unique-agent-strategy.md](unique-agent-strategy.md). This file records the current shape of the system and what to pick up next.
>
> Update at the end of every session that advances the roadmap.

## Current state

The capability vocabulary is 6 verbs, all enforced through one shared authorizer:

| Verb | Manifest declaration | Runtime enforcement |
|---|---|---|
| `tool:<name>` / `tool:*` | yes | `CapabilityAuthorizer` from `src/Tools/Weave.Tools/Actors/ToolActor.cs` |
| `secret:<path>` | yes | `CapabilityAuthorizer` from `src/Security/Weave.Security/Vault/VaultSecretProvider.cs` and `InMemorySecretProvider.cs` |
| `skill:read` / `skill:write` | yes | `CapabilityAuthorizer` from `src/Assistants/Weave.Agents/Actors/SkillMemoryActor.cs` |
| `channel:send:<id>` / `channel:receive:<id>` | yes | `CapabilityAuthorizer` from `src/Assistants/Weave.Agents/Actors/ChannelGatewayActor.cs` |
| `user:read:<userId>` / `user:write:<userId>` | yes | `CapabilityAuthorizer` from `src/Assistants/Weave.Agents/Actors/UserModelActor.cs` |
| `plugin:invoke:<plugin>` | yes | `CapabilityAuthorizer` from `src/Runtime/Weave.Silo/Plugins/PluginRegistry.cs` |
| `marketplace:install` | not implemented — see Next work | — |

Tests: 1835 passed.

### How a verb is wired today

Six sites that previously held a private `Authorize(...)` method (or, in the case of the secret providers, an inline `Validate` + `HasGrant` pair) now share `ICapabilityAuthorizer` ([CapabilityAuthorizer.cs](../src/Security/Weave.Security/Tokens/CapabilityAuthorizer.cs)). The authorizer:

1. **Validates** signature, expiry, revocation via `ICapabilityTokenService.Validate`.
2. **Workspace-matches** when `actorWorkspaceId` is non-empty (PluginRegistry passes `null` since plugins are silo-wide).
3. **Grant-checks** via `CapabilityToken.HasGrant` (segment-wise wildcards).
4. **Publishes** `CapabilityAuthorizationEvent` ([CapabilityAuthorizationEvent.cs](../src/Security/Weave.Security/Events/CapabilityAuthorizationEvent.cs)) on every call (allow OR deny) keyed by `tokenId`, `grant`, `workspaceId`, `issuedTo`, `outcome`, `actionContext`, and `reason` on denies (`"invalid-or-expired-token"` / `"workspace-mismatch"` / `"grant-missing"`).
5. **Throws** `UnauthorizedAccessException` on deny.

Call sites pass `actionContext` as a literal string (e.g. `"ChannelGatewayActor.RouteInbound:receive"`) or default to `[CallerMemberName]`.

DI registration lives in `src/Runtime/Weave.Silo/Startup/SiloServiceRegistrar.cs` `RegisterSecurity()`. Per-request token minting at the API boundary, manifest-gated runtime mints, and cancellation propagation are unchanged.

### How replay/debugger is wired today

Roadmap #3 ships as a thin pipeline on top of the audit event. No producer-side changes:

1. **Store** ([`InMemoryCapabilityAuditStore.cs`](../src/Security/Weave.Security/Audit/InMemoryCapabilityAuditStore.cs)) — singleton, capacity-bounded LinkedList behind a `Lock`. FIFO eviction at `CapabilityAudit:Capacity` (default 10,000). `GetByToken(tokenId)` filters chronologically; `GetRecent(limit)` walks newest-first.
2. **Subscriber** ([`CapabilityAuditSubscriberHostedService.cs`](../src/Runtime/Weave.Silo/Audit/CapabilityAuditSubscriberHostedService.cs)) — `IHostedService` that subscribes to `IEventBus` for `CapabilityAuthorizationEvent` in `StartAsync` and disposes in `StopAsync`. Single forward-to-store handler.
3. **Queries** — `GetCapabilityAuditByTokenQuery(tokenId)` and `GetRecentCapabilityAuditQuery(limit)` in `src/Security/Weave.Security/Queries/`. Picked up by source-generated CQRS registration like every other query handler.
4. **HTTP** — `GET /api/audit/capability/{tokenId}` and `GET /api/audit/capability?limit=N` ([`AuditEndpoints.cs`](../src/Runtime/Weave.Silo/Api/AuditEndpoints.cs)). Returns `CapabilityAuditEntryResponse[]` (`outcome` as string for stability).
5. **CLI** — `weave audit replay [tokenId]` ([`AuditReplayCliCommand.cs`](../src/UX/Weave.Cli/Commands/Audit/AuditReplayCliCommand.cs)). Guided mode: zero-arg invocation lists distinct recent tokens via Spectre `SelectionPrompt`; advanced mode: pass the tokenId. Renders allow/deny trace as a Spectre table coloured by outcome.
6. **Dashboard** — `/audit` and `/audit/{tokenId}` ([`Audit.razor`](../src/UX/Weave.Dashboard/Pages/Audit.razor)). Defaults to recent rows; deep links to per-token replay; token cells link back to the per-token view. Uses `WeaveApiClient.GetRecentCapabilityAuditAsync` / `GetCapabilityAuditByTokenAsync` so any future backend swap (durable store) is invisible to the page.

Acceptance bar from the strategy doc's *Measurement* section is met:
- **Audit completeness** — every Authorize call (allow + deny) publishes a row carrying capability, grant, actor, reason. Verified by the per-actor smoke tests added in roadmap #2 + the end-to-end `CapabilityAuditEndpointTests`.
- **Coverage of action types** — 6/6 actor sites publish through the shared authorizer.

## Next work

**Don't pursue `marketplace:install` yet** — `IMarketplaceActor.IncrementInstallCountAsync` is a counter, not an install path. Gating an action that doesn't exist is empty ceremony. Wait until someone wires real marketplace-to-workspace installation, then gate it through the same authorizer.

Natural next moves, in order of leverage:

- **Durable audit store.** The in-memory store is fine for replay/debug but evicts under load. A SQLite or Postgres-backed `ICapabilityAuditStore` (selected via `CapabilityAuditOptions.Backend`) is a small, mechanical follow-up.

## Cross-cutting follow-ups

These remain real gaps. None blocks the *Next work* items above.

- **`ToolRegistryConnector` self-mints `[$"tool:{toolName}", "secret:*"]`** without consulting the agent's manifest. The connector is workspace-scoped (no `AgentDefinition` in scope), so the manifest-gate fix is less obvious. Likely shape: pass the requesting agent's capabilities through, or treat tool registration as a workspace-admin verb gated by a separate grant.
- **`channel:send:*` requires both grants today** because `RouteInboundAsync` does both ingress and reply atomically. The double `Authorize` call now lives at the call site (lines 74–75 of ChannelGatewayActor) with distinct `actionContext` strings (`":receive"` / `":send"`), so the audit log distinguishes them. If a webhook adapter ever needs receive-only, split the actor surface.

## Template to mirror

For the next vocabulary entry (or any follow-up that touches the audit pipeline):

| What | Where |
|---|---|
| Single authorizer with Authorize signature | `src/Security/Weave.Security/Tokens/CapabilityAuthorizer.cs` |
| Audit event record | `src/Security/Weave.Security/Events/CapabilityAuthorizationEvent.cs` |
| In-memory audit store + bounded options | `src/Security/Weave.Security/Audit/InMemoryCapabilityAuditStore.cs` |
| Subscriber hosted service | `src/Runtime/Weave.Silo/Audit/CapabilityAuditSubscriberHostedService.cs` |
| Per-actor call-site shape | `await authorizer.AuthorizeAsync(token, grant, actorWorkspaceId, actionContext);` |
| DI seam | `SiloServiceRegistrar.RegisterSecurity()` (store) + `RegisterAgentPipeline()` (hosted service) |
| Query records + handlers | `src/Security/Weave.Security/Queries/Get*CapabilityAuditQuery.cs` |
| HTTP endpoint group | `src/Runtime/Weave.Silo/Api/AuditEndpoints.cs` (mapped in `SiloApplicationConfigurator.MapEndpoints`) |
| CLI command pair | `src/UX/Weave.Cli/Commands/Audit/AuditReplayCliCommand.cs` + `AuditReplayCommand.cs` (wired in `Program.cs`) |
| End-to-end audit test | `src/Runtime/Weave.Silo.Tests/Audit/CapabilityAuditEndpointTests.cs` |

## History

- **2026-05-03** (`claude/implement-next-task-vSX6x`) — capability audit dashboard view: `/audit` (recent rows) and `/audit/{tokenId}` (per-token replay) Blazor pages, fed by new `WeaveApiClient.GetRecentCapabilityAuditAsync` / `GetCapabilityAuditByTokenAsync` over the existing `/api/audit/capability` endpoints. Token cells deep-link to per-token replay. No backend changes; tests still 1835
- **2026-05-03** (`claude/implement-next-task-vSX6x`) — manifest-side wildcard matching: added static `CapabilityToken.HasGrant(IEnumerable<string>, string)`; replaced the four `state.Definition.Capabilities.Contains(grant)` literal checks (`AgentChatPipeline.EnrichWithUserContextAsync`, `SkillMemoryPromptEnricher.EnrichAsync` / `RecordSuccessfulUsageAsync`, `AgentSkillSuggester.SuggestFromTaskAsync`) with the segment-wise wildcard match. A manifest declaring `skill:*` now grants `skill:read`/`skill:write` for the manifest gate; tests 1827 → 1835
- **2026-05-03** (`claude/implement-next-task-vSX6x`) — `secret:<path>` enforcement moved out of `VaultSecretProvider` / `InMemorySecretProvider` into the shared `CapabilityAuthorizer`; allow + deny rows now flow through the audit pipeline, closing the last *Coverage of action types* gap (5/6 → 6/6); tests 1820 → 1827
- **2026-05-03** (`claude/competitor-analysis-handoff-Qn9jh`) — capability replay/debugger shipped; in-memory `ICapabilityAuditStore` (capacity-bounded) fed by a subscriber hosted service; `GET /api/audit/capability/{tokenId}` and `?limit=N` over CQRS query handlers; `weave audit replay [tokenId]` CLI with guided + advanced modes; tests 1807 → 1820
- **2026-05-03** (`claude/competitor-analysis-handoff-Qn9jh`) — capability-bound audit log shipped; five `Authorize` copies consolidated into `ICapabilityAuthorizer`; allow + deny rows publish `CapabilityAuthorizationEvent` keyed by tokenId/grant/workspaceId/issuedTo/outcome/reason/actionContext; tests 1790 → 1807
- **2026-05-03** (`claude/continue-agent-strategy-yS2Z1`) — `channel:*`, `user:*`, `plugin:invoke:*` shipped; capability cancellation linkage; mid-segment wildcards; `IReadOnlyList<T>` manifest cleanup; four PR #40 follow-ups; plugin types moved Workspaces → Silo to enable the verb
- **2026-05-03** — `skill:read` / `skill:write` shipped (PR #40)
- **2026-05-03** — Strategy doc + best-practices audit (PR #39)
