# Handoff — Capability Vocabulary Expansion

> **Live state only.** The contract — principles, vocabulary table, roadmap — lives in [unique-agent-strategy.md](unique-agent-strategy.md). This file records what's in flight and what to pick up next.
>
> Update at the end of every session that advances the roadmap.

## Last session (2026-05-03)

**Shipped:**
- `channel:send` / `channel:receive` — second verb of [unique-agent-strategy.md §"Roadmap implications"](unique-agent-strategy.md#roadmap-implications)
- `user:read:<userId>` / `user:write:<userId>` — third verb
- `plugin:invoke:<plugin>` — fourth verb (gates plugin connect/disconnect at the registry)
- `IReadOnlyList<T>` cleanup across immutable manifest + event collections
- Four PR #40 cross-cutting follow-ups (cross-workspace token check, manifest-derived grants, denial-path logging, null-forgiving fix)
- **Capability cancellation linkage**: `CapabilityToken.CancellationToken` + `CapabilityTokenSource` + revocation registry. In-flight operations now cancel when the parent request aborts, the token expires, or the token is revoked.
- **Mid-segment wildcards** in token grants: `user:*:alice` matches `user:read:alice` and `user:write:alice`; trailing `*` still covers one-or-more segments.

Branch: `claude/continue-agent-strategy-yS2Z1`. Tests: 1790 passed, 0 failed.

### Plugin verb (and the move that made it possible)

`IPluginRegistry` and friends moved from `Weave.Workspaces.Plugins` to `Weave.Silo.Plugins` (along with the test file, which moved to `Weave.Silo.Tests`). The dependency flow `Shared → Workspaces → Security → ...` had blocked adding a `CapabilityToken` parameter to the registry interface — Workspaces sits before Security and can't import from it. Moving to Silo (downstream of Security) lets the interface take a token.

After the move:
- `IPluginRegistry.ConnectAsync` / `DisconnectAsync` / `ConnectAllAsync` take a `CapabilityToken`
- `PluginRegistry.Authorize(token, pluginName)` validates signature + grant of `plugin:invoke:<name>`
- `PluginTokenFactory.MintInvoke(svc, name, ct)` mirrors `SkillTokenFactory` / `ChannelTokenFactory` / `UserTokenFactory`
- `PluginEndpoints` mints a per-request token via `using var source = ...` and passes `source.Token`
- `SiloPluginActivator` mints a startup-scoped token (workspace `silo`, issued-to `silo/plugin-activator`) per plugin name during boot

Cross-workspace check is intentionally NOT part of the plugin Authorize: the registry is silo-singleton, not workspace-scoped. The token's workspace is for audit context only.

`InternalsVisibleTo("Weave.Silo.Tests")` was added to `Weave.Silo.csproj` so the `PluginConfigResolver` tests (which use `internal` resolver helpers) work in their new home.

### Capability cancellation linkage (Option C)

`CapabilityToken` gained a `CancellationToken CancellationToken { get; init; }` property (`[JsonIgnore]`, defaults to `None`, not serialized — Orleans handles cross-grain calls by losing the linkage and defaulting on the receiver). `CapabilityTokenSource` is the `using`-disposable wrapper that pairs the token with a `CancellationTokenSource` linked to: parent CT, expiry timer, and a per-token revocation registry on `CapabilityTokenService`.

`ICapabilityTokenService.MintLinked(request, parentCt)` returns the source. `Mint` still exists for tokens that escape their issuance scope (e.g., `ToolRegistryActor.ResolveAsync` returns the token in a `ToolResolution` for the agent to use later — no enclosing scope to bind).

Endpoints and internal mint sites use `using var source = ...MintLinked(...);` and pass `source.Token`. Token-gated actor methods pass `token.CancellationToken` to downstream `eventBus.PublishAsync(...)`, `persistentState.WriteStateAsync(...)`, and lifecycle hook calls. Calling `tokenService.Revoke(tokenId)` cancels any active source for that token — no more "validate-on-next-call" lag.

Coverage: four new tests in `CapabilityTokenServiceTests` for parent-cancel, revocation, expiry-already-cancelled, dispose-cleanup behavior.

### Wildcard handling

Segment-wise wildcards in `CapabilityToken.HasGrant`. Each `*` covers one segment; trailing `*` covers one-or-more. Examples: `tool:*` matches any depth under `tool`; `user:*:alice` matches `user:read:alice` and `user:write:alice`; the bare `*` matches anything. `ToolActor` dropped its explicit `tool:*` second-check long ago.

Manifest-side wildcards (e.g., a manifest declaring `skill:*` to grant both `skill:read` and `skill:write` to the runtime mint sites) are NOT supported yet — `AgentSkillSuggester` and `SkillMemoryPromptEnricher` use literal `.Contains(grant)`. Add when a use case requires it.

The per-actor `Authorize` private method (validate, workspace-match, grant-check, log-deny, throw) is duplicated across `SkillMemoryActor`, `ChannelGatewayActor`, `UserModelActor`, `ToolActor`. ~12 lines each. Four copies now — still under the abstraction threshold, but worth flagging if a fifth verb arrives.

## Next work

Only one vocabulary entry remains: **`marketplace:install`**. Wait until an actual install path is built — `IMarketplaceActor.IncrementInstallCountAsync` is just a counter today, not a real installation flow. Gating an action that doesn't exist is empty ceremony.

After that, the vocabulary table is fully Today. Future work shifts to:
- The capability-bound audit log (#2 on the roadmap)
- The capability replay/debugger (#3 on the roadmap)

## Cross-cutting follow-ups still open

- **`ToolRegistryConnector` self-mints `[$"tool:{toolName}", "secret:*"]`** without consulting the agent's manifest. The connector is workspace-scoped (no `AgentDefinition` in scope), so the routing-through-capabilities fix is less obvious. Likely fix: pass the requesting agent's capabilities through, or treat tool registration as a workspace-admin verb gated by a separate grant.
- **`channel:send:*` requires both grants today** because `RouteInboundAsync` does both ingress and reply atomically. If a webhook adapter ever needs receive-only (forward to a queue, no reply), split the actor surface.
- **Manifest-side wildcards** — runtime mint sites use literal `.Contains(grant)` against `state.Definition.Capabilities`. If the manifest ever declares `skill:*`, that doesn't currently grant `skill:read`/`skill:write` for the runtime gate. Wire `CapabilityToken`-style segment matching into the manifest check when needed.

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
