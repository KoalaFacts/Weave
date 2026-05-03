# Handoff — Capability Vocabulary Expansion

> **Live state only.** The contract — principles, vocabulary table, roadmap — lives in [unique-agent-strategy.md](unique-agent-strategy.md). This file records the current shape of the system and what to pick up next.
>
> Update at the end of every session that advances the roadmap.

## Current state

The capability vocabulary is 4-of-5 Today:

| Verb | Enforced at |
|---|---|
| `tool:<name>` / `tool:*` | `src/Tools/Weave.Tools/Actors/ToolActor.cs` |
| `secret:<path>` | `src/Security/Weave.Security/Actors/SecretProxyActor.cs` |
| `skill:read` / `skill:write` | `src/Assistants/Weave.Agents/Actors/SkillMemoryActor.cs` |
| `channel:send:<id>` / `channel:receive:<id>` | `src/Assistants/Weave.Agents/Actors/ChannelGatewayActor.cs` |
| `user:read:<userId>` / `user:write:<userId>` | `src/Assistants/Weave.Agents/Actors/UserModelActor.cs` |
| `plugin:invoke:<plugin>` | `src/Runtime/Weave.Silo/Plugins/PluginRegistry.cs` |
| `marketplace:install` | not implemented — see Next work |

Tests: 1790 passed. Branch (in flight): `claude/continue-agent-strategy-yS2Z1`.

### How a verb is wired today

1. **Authorize at the boundary.** A private `Authorize(token, …)` method on the actor / registry validates signature, workspace match, and grant. Logs the deny with `LogWarning` and throws `UnauthorizedAccessException`. Five copies live in `ToolActor`, `SkillMemoryActor`, `ChannelGatewayActor`, `UserModelActor`, `PluginRegistry` — all near-identical (see "Cross-cutting" below).
2. **Per-request token at the API.** `XxxTokenFactory.MintXxx(svc, ws, ct)` returns a `CapabilityTokenSource`. Endpoints do `using var source = ...; pass source.Token`. Internal mint sites (e.g., `AgentSkillSuggester`) use the same shape with `MintLinked(req, CancellationToken.None)`.
3. **Cancellation propagates.** `CapabilityToken.CancellationToken` fires when the parent CT cancels, the token expires, or `tokenService.Revoke(tokenId)` is called. Token-gated actor methods pass `token.CancellationToken` to `eventBus.PublishAsync`, `persistentState.WriteStateAsync`, and lifecycle hooks.
4. **Manifest gate for runtime mints.** `AgentSkillSuggester` and `SkillMemoryPromptEnricher` check `state.Definition?.Capabilities?.Contains(grant)` before minting — runtime can't elevate beyond what the manifest declares (principle 3).
5. **Wildcards.** `CapabilityToken.HasGrant` matches segment-wise: `*` covers one segment; trailing `*` covers one-or-more. So `tool:*` matches `tool:foo` and `tool:foo:bar`; `user:*:alice` matches `user:read:alice` and `user:write:alice`; `*` matches anything.

## Next work

**Don't pursue `marketplace:install` yet** — `IMarketplaceActor.IncrementInstallCountAsync` is a counter, not an install path. Gating an action that doesn't exist is empty ceremony. Wait until someone wires real marketplace-to-workspace installation, then gate it.

The vocabulary phase is effectively done. Roadmap items #2 and #3 are the natural next moves:

- **#2 Capability-bound audit log.** Every Authorize call (allow OR deny) emits a structured row keyed by `tokenId`, `grant`, `workspaceId`, `issuedTo`, `outcome`. The deny-side logging exists in five places already (`LogWarning`); the allow-side doesn't. Consolidating both into one audit sink makes principle 2 ("fail closed, log loud") measurable and is a natural place to deduplicate the five `Authorize` copies.
- **#3 Capability replay/debugger.** Given a `tokenId`, surface the action it authorized + the deny/allow trace. Depends on #2 landing first.

## Cross-cutting follow-ups

These are real gaps. Lifting them as a single PR before the next vocabulary entry is cheap; afterwards they compound.

- **Five `Authorize` copies past the abstraction threshold.** CLAUDE.md says three similar lines is fine; five copies of ~15 lines is not. The natural shape is a shared `CapabilityAuthorizer` (which I extracted earlier in this session and reverted at three copies). At five, it's the right call. **Couple this with item #2** — the audit-log work has to touch every Authorize site anyway.
- **`ToolRegistryConnector` self-mints `[$"tool:{toolName}", "secret:*"]`** without consulting the agent's manifest. The connector is workspace-scoped (no `AgentDefinition` in scope), so the manifest-gate fix is less obvious. Likely shape: pass the requesting agent's capabilities through, or treat tool registration as a workspace-admin verb gated by a separate grant.
- **`channel:send:*` requires both grants today** because `RouteInboundAsync` does both ingress and reply atomically. If a webhook adapter ever needs receive-only (forward to a queue, no reply), split the actor surface.
- **Manifest-side wildcards.** Runtime mint sites use literal `.Contains(grant)` against `state.Definition.Capabilities`. A manifest declaring `skill:*` doesn't grant `skill:read`/`skill:write` for the runtime gate — the manifest check ignores wildcards. Wire `CapabilityToken`-style segment matching into the manifest check when needed.

## Template to mirror

For the next verb (or whoever consolidates Authorize):

| What | Where |
|---|---|
| Actor + private `Authorize` | `src/Assistants/Weave.Agents/Actors/ChannelGatewayActor.cs` |
| Per-request token at API | `src/Runtime/Weave.Silo/Api/ChannelTokenFactory.cs` |
| CQRS record carrying token | `src/Assistants/Weave.Agents/Commands/RouteInboundMessageCommand.cs` |
| Endpoint shape | `using var source = XxxTokenFactory.MintXxx(svc, ws, ct);` then pass `source.Token` |
| Internal mint with manifest gate + linked CT | `src/Assistants/Weave.Agents/Actors/AgentSkillSuggester.cs` |
| Threading `token.CancellationToken` | `src/Assistants/Weave.Agents/Actors/SkillMemoryActor.cs` |
| Capability test cases | `ChannelGatewayActorTests`: `_Without*Grant_Throws`, `_WithExpiredToken_Throws`, `_WithCrossWorkspaceToken_Throws`, `_WithReceiveSendWildcardGrants_Allowed`, `_WithChannelSpecificGrants_AllowsOnlyMatchingChannel` |
| Cancellation-linkage tests | `CapabilityTokenServiceTests`: `MintLinked_*` |

## History

- **2026-05-03** (`claude/continue-agent-strategy-yS2Z1`) — `channel:*`, `user:*`, `plugin:invoke:*` shipped; capability cancellation linkage; mid-segment wildcards; `IReadOnlyList<T>` manifest cleanup; four PR #40 follow-ups; plugin types moved Workspaces → Silo to enable the verb
- **2026-05-03** — `skill:read` / `skill:write` shipped (PR #40)
- **2026-05-03** — Strategy doc + best-practices audit (PR #39)
