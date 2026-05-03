# Handoff — Capability Vocabulary Expansion

> **Live state only.** The contract — principles, vocabulary table, roadmap — lives in [unique-agent-strategy.md](unique-agent-strategy.md). This file records what's in flight and what to pick up next.
>
> Update at the end of every session that advances the roadmap.

## Last session (2026-05-03)

**Shipped: `channel:send` / `channel:receive`** — second verb of [unique-agent-strategy.md §"Roadmap implications"](unique-agent-strategy.md#roadmap-implications) item #1. Plus the four PR #40 follow-ups, lifted before the vocabulary expanded further.

- Branch `claude/continue-agent-strategy-yS2Z1`
- Tests: 1778 passed, 0 failed (was 1766 — 12 new tests)
- Strategy doc: `channel:send` / `channel:receive` row moved Direction → Today; `ChannelGatewayActor.cs` cited; wildcard rule grounded in `CapabilityGrants` and `CapabilityAuthorizer`

### Cross-cutting follow-ups (lifted in this session)

1. **Cross-workspace token check** — added in `CapabilityAuthorizer.Authorize`; covered by tests in `SkillMemoryActorTests`, `ToolActorTests`, `ChannelGatewayActorTests`. `SkillMemoryActor` now also bootstraps state.WorkspaceId from the grain key (`OnActivatedAsync`); `SkillMemoryActorGrain` wired to call it.
2. **Manifest-derived grants** — `AgentSkillSuggester` and `SkillMemoryPromptEnricher` now consult `state.Definition?.Capabilities` (via `CapabilityGrants.Matches`) before minting; covered by `ReviewTaskAsync_Accepted_WithoutSkillWriteCapability_SkipsSuggestion` and `ExecuteAsync_WithoutSkillReadCapability_SkipsSkillEnrichment`.
3. **Denial-path logging** — every deny path emits a `LogWarning` from one place (`CapabilityAuthorizer`).
4. **`AgentSkillSuggester` null-forgiving** — removed; `ExtractFromTask`'s null contract is the only precondition now.

### De-duplication

The follow-up work surfaced three separate copies of the same Authorize logic and two copies of wildcard-walking. Both consolidated:

- One predicate: `CapabilityGrants.Matches(grants, requested)` — used by both `CapabilityToken.HasGrant` and the manifest check. `tool:*`, `channel:send:*`, and `*` all match through it.
- One authorizer: `CapabilityAuthorizer.Authorize(...)` — validate, workspace-match, grant-check, log-deny, throw. Called by `SkillMemoryActor`, `ChannelGatewayActor`, `ToolActor`. `ToolActor` lost its three per-instance LoggerMessage helpers and the explicit `tool:*` second-check (now handled by `HasGrant`).

## Next work

Pick the next verb from the [vocabulary table](unique-agent-strategy.md#capability-vocabulary). Recommended order (cheapest first):

1. **`user:read:<userId>` / `user:write:<userId>`** — `UserModelActor`. The `user:*:alice` mid-segment wildcard is still unimplemented; `CapabilityGrants.Matches` only handles trailing wildcards. Decision: extend `CapabilityGrants` or scope the user verb to trailing wildcards only.
2. **`plugin:invoke:<plugin>`** — `DaprToolConnector`, `VaultSecretProvider`, future webhook plugins. Lower priority (not LLM-reachable).
3. **`marketplace:install`** — when the install path materializes.

**Branch off `main`** for the next verb.

## Cross-cutting follow-ups still open

- **`ToolRegistryConnector` self-mints `[$"tool:{toolName}", "secret:*"]`** without consulting the agent's manifest. The connector is workspace-scoped (no `AgentDefinition` in scope), so the routing-through-capabilities fix is less obvious here than it was for the per-agent sites. Likely fix: pass the requesting agent's capabilities through, or treat tool registration as a workspace-admin verb gated by a separate grant.
- **`channel:send:*` semantics today require a token to hold both `channel:receive:<id>` and `channel:send:<id>`** because `RouteInboundAsync` does both ingress and reply atomically. If a webhook adapter ever needs receive-only (forward to a queue, no reply), the actor surface must split.

## Template to mirror

For the next verb, copy the shape of:
- **Actor + grant check via `CapabilityAuthorizer`**: `src/Assistants/Weave.Agents/Actors/ChannelGatewayActor.cs` (`Authorize` helper is one line)
- **Per-request token mint at API**: `src/Runtime/Weave.Silo/Api/ChannelTokenFactory.cs` (mirrors `SkillTokenFactory`)
- **CQRS record carrying token**: `src/Assistants/Weave.Agents/Commands/RouteInboundMessageCommand.cs`
- **Internal mint at runtime call site (with manifest gate)**: `src/Assistants/Weave.Agents/Actors/AgentSkillSuggester.cs`
- **Capability tests**: the six `_Without*Grant_Throws` / `_WithExpiredToken_Throws` / `_WithCrossWorkspaceToken_Throws` / `_WithReceiveSendWildcardGrants_Allowed` / `_WithChannelSpecificGrants_AllowsOnlyMatchingChannel` cases in `src/Assistants/Weave.Agents.Tests/ChannelGatewayActorTests.cs`

## History

- **2026-05-03** — `channel:send` / `channel:receive` shipped + four PR #40 follow-ups + helper consolidation
- **2026-05-03** — `skill:read` / `skill:write` shipped (PR #40)
- **2026-05-03** — Strategy doc + best-practices audit (PR #39)
