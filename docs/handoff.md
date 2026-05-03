# Handoff — Capability Vocabulary Expansion

> **Live state only.** The contract — principles, vocabulary table, roadmap — lives in [unique-agent-strategy.md](unique-agent-strategy.md). This file records what's in flight and what to pick up next.
>
> Update at the end of every session that advances the roadmap.

## Last session (2026-05-03)

**Shipped: `skill:read` / `skill:write`** — first verb of [unique-agent-strategy.md §"Roadmap implications"](unique-agent-strategy.md#roadmap-implications) item #1.

- PR: [#40](https://github.com/KoalaFacts/Weave/pull/40) — branch `claude/review-agent-strategy-qwb2x`, head `6ac7528`
- CI: all 10 checks green
- Tests: 1766 passed, 0 failed
- Coverage: 100% on every PR-touched file (682 / 682 lines)
- Strategy doc: `skill:write` / `skill:read` row moved Direction → Today; `SkillMemoryActor.cs` cited

## Next work

Pick the next verb from the [vocabulary table](unique-agent-strategy.md#capability-vocabulary). Recommended order (cheapest first):

1. **`channel:send` / `channel:receive`** — `ChannelGatewayActor` + channel webhooks. Same shape as `skill:*`. Bounded scope (5 channels).
2. **`user:read:<userId>` / `user:write:<userId>`** — `UserModelActor`. Decision needed on `user:*:alice` pattern matching (the strategy doc already promises it works).
3. **`plugin:invoke:<plugin>`** — `DaprToolConnector`, `VaultSecretProvider`, future webhook plugins. Lower priority (not LLM-reachable).
4. **`marketplace:install`** — when the install path materializes.

**Branch off `main`** for the next verb. Don't reuse `claude/review-agent-strategy-qwb2x` (that's PR #40).

## Cross-cutting follow-ups (apply to every existing verb)

Surfaced by PR #40's review. Lift these before the vocabulary expands further — they compound with each new verb.

1. **Cross-workspace token check.** `Authorize` validates signature + grant but doesn't verify `token.WorkspaceId` matches the actor's workspace. A valid token for workspace A authorizes actions on B's actor today. Same gap in `ToolActor.cs:34-41`. One-line fix in both.
2. **Manifest-derived grants.** `AgentSkillSuggester` and `SkillMemoryPromptEnricher` mint tokens with hard-coded grants. The strategy doc's principle 3 says the manifest is the source of truth — runtime mints regardless. `ToolRegistryConnector` has the same pattern. Fix: route through `AgentDefinition.Capabilities` before minting.
3. **Denial-path logging.** `Authorize` throws but doesn't log. Strategy doc principle 2 mandates audit emission on every deny. Until item #2 of the roadmap (full audit log) lands, a `LogWarning(...)` per deny path makes operations visible.
4. **`AgentSkillSuggester` null-forgiving operator.** `var skill = AgentSkillExtractor.ExtractFromTask(task, state)!;` couples to the caller's precondition implicitly. Either tighten `ExtractFromTask` to return non-null, or add a one-line comment naming the contract.

## Template to mirror

For the next verb, copy the shape of:
- **Actor + grant check**: `src/Assistants/Weave.Agents/Actors/SkillMemoryActor.cs` (`Authorize` helper)
- **Per-request token mint at API**: `src/Runtime/Weave.Silo/Api/SkillTokenFactory.cs`
- **CQRS record carrying token**: `src/Assistants/Weave.Agents/Commands/StoreSkillCommand.cs`
- **Internal mint at runtime call site**: `src/Assistants/Weave.Agents/Actors/AgentSkillSuggester.cs`
- **Capability tests**: the four `_WithoutXGrant_Throws` / `_WithExpiredToken_Throws` / `_WithWildcardGrant_Allowed` cases in `src/Assistants/Weave.Agents.Tests/SkillMemoryActorTests.cs`

## History

- **2026-05-03** — `skill:read` / `skill:write` shipped (PR #40)
- **2026-05-03** — Strategy doc + best-practices audit (PR #39)
