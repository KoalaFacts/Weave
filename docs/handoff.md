# Handoff — Capability Vocabulary Expansion

> **Live state only.** The contract — principles, vocabulary table, roadmap — lives in [unique-agent-strategy.md](unique-agent-strategy.md). This file records what's in flight and what to pick up next.
>
> Update at the end of every session that advances the roadmap.

## Last session (2026-05-03)

**Shipped:**
- `channel:send` / `channel:receive` — second verb of [unique-agent-strategy.md §"Roadmap implications"](unique-agent-strategy.md#roadmap-implications)
- `user:read:<userId>` / `user:write:<userId>` — third verb
- `IReadOnlyList<T>` cleanup across immutable manifest + event collections
- Four PR #40 cross-cutting follow-ups (cross-workspace token check, manifest-derived grants, denial-path logging, null-forgiving fix)
- **Capability cancellation linkage**: `CapabilityToken.CancellationToken` + `CapabilityTokenSource` + revocation registry. In-flight operations now cancel when the parent request aborts, the token expires, or the token is revoked.

Branch: `claude/continue-agent-strategy-yS2Z1`. Tests: 1782 passed, 0 failed.

### Capability cancellation linkage (Option C)

`CapabilityToken` gained a `CancellationToken CancellationToken { get; init; }` property (`[JsonIgnore]`, defaults to `None`, not serialized — Orleans handles cross-grain calls by losing the linkage and defaulting on the receiver). `CapabilityTokenSource` is the `using`-disposable wrapper that pairs the token with a `CancellationTokenSource` linked to: parent CT, expiry timer, and a per-token revocation registry on `CapabilityTokenService`.

`ICapabilityTokenService.MintLinked(request, parentCt)` returns the source. `Mint` still exists for tokens that escape their issuance scope (e.g., `ToolRegistryActor.ResolveAsync` returns the token in a `ToolResolution` for the agent to use later — no enclosing scope to bind).

Endpoints and internal mint sites use `using var source = ...MintLinked(...);` and pass `source.Token`. Token-gated actor methods pass `token.CancellationToken` to downstream `eventBus.PublishAsync(...)`, `persistentState.WriteStateAsync(...)`, and lifecycle hook calls. Calling `tokenService.Revoke(tokenId)` cancels any active source for that token — no more "validate-on-next-call" lag.

Coverage: four new tests in `CapabilityTokenServiceTests` for parent-cancel, revocation, expiry-already-cancelled, dispose-cleanup behavior.

### Wildcard handling

Trailing-segment wildcards (`tool:*`, `channel:send:*`, `*`) live inside `CapabilityToken.HasGrant`. `ToolActor` dropped its old explicit `tool:*` second-check.

The per-actor `Authorize` private method (validate, workspace-match, grant-check, log-deny, throw) is duplicated across `SkillMemoryActor`, `ChannelGatewayActor`, `UserModelActor`, `ToolActor`. ~12 lines each. Earlier in the session I extracted a shared `CapabilityAuthorizer`; reverted because three private copies follows the existing pattern (and CLAUDE.md: "Three similar lines is better than a premature abstraction"). Four copies now — still under the abstraction threshold, but worth flagging if a fifth verb arrives.

## Next work

Pick the next verb from the [vocabulary table](unique-agent-strategy.md#capability-vocabulary). Recommended order:

1. **`plugin:invoke:<plugin>`** — `DaprToolConnector`, `VaultSecretProvider`, future webhook plugins. Lower priority (not LLM-reachable).
2. **`marketplace:install`** — when the install path materializes.
3. **Mid-segment wildcards** (`user:*:alice`) — promised in the strategy doc, not implemented. `CapabilityToken.HasGrant` handles trailing wildcards only. Add when a use case appears.

## Cross-cutting follow-ups still open

- **`ToolRegistryConnector` self-mints `[$"tool:{toolName}", "secret:*"]`** without consulting the agent's manifest. The connector is workspace-scoped (no `AgentDefinition` in scope), so the routing-through-capabilities fix is less obvious. Likely fix: pass the requesting agent's capabilities through, or treat tool registration as a workspace-admin verb gated by a separate grant.
- **`channel:send:*` requires both grants today** because `RouteInboundAsync` does both ingress and reply atomically. If a webhook adapter ever needs receive-only (forward to a queue, no reply), split the actor surface.
- **Mid-segment wildcards** as noted above.

## Template to mirror

For the next verb:
- **Actor + private `Authorize`**: `src/Assistants/Weave.Agents/Actors/ChannelGatewayActor.cs`
- **Per-request token mint at API (returns `CapabilityTokenSource`)**: `src/Runtime/Weave.Silo/Api/ChannelTokenFactory.cs`
- **CQRS record carrying token**: `src/Assistants/Weave.Agents/Commands/RouteInboundMessageCommand.cs`
- **Endpoint pattern**: `using var source = XxxTokenFactory.MintXxx(svc, ws, ct); ... command with source.Token`
- **Internal mint with manifest gate + linked CT**: `src/Assistants/Weave.Agents/Actors/AgentSkillSuggester.cs`
- **Actor body — pass `token.CancellationToken` to eventBus/state I/O**: `src/Assistants/Weave.Agents/Actors/SkillMemoryActor.cs`
- **Capability tests**: `src/Assistants/Weave.Agents.Tests/ChannelGatewayActorTests.cs` (`_Without*Grant_Throws`, `_WithExpiredToken_Throws`, `_WithCrossWorkspaceToken_Throws`, `_WithReceiveSendWildcardGrants_Allowed`, `_WithChannelSpecificGrants_AllowsOnlyMatchingChannel`)
- **Cancellation-linkage tests**: `src/Security/Weave.Security.Tests/CapabilityTokenServiceTests.cs` (`MintLinked_*`)

## History

- **2026-05-03** — `channel:send` / `channel:receive` + `user:read` / `user:write` shipped, capability cancellation linkage (Option C), `IReadOnlyList<T>` manifest cleanup, four PR #40 follow-ups
- **2026-05-03** — `skill:read` / `skill:write` shipped (PR #40)
- **2026-05-03** — Strategy doc + best-practices audit (PR #39)
