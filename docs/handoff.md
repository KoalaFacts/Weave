# Handoff — Capability Vocabulary Expansion

> **Living document.** Update this file at the end of every session that advances the [unique-agent-strategy.md](unique-agent-strategy.md) roadmap. Append to history; rewrite "Current state" and "Next work" in place.

## Strategy reference

The capability-first roadmap lives in [unique-agent-strategy.md](unique-agent-strategy.md) §"Roadmap implications". Three top-priority items:

| # | Item | Status |
|---|---|---|
| 1 | **Capability vocabulary expansion** — new grant strings (`channel:send`, `skill:write`, `user:read`, `plugin:invoke`, …) | **In progress** — `skill:read` / `skill:write` shipped (PR #40) |
| 2 | **Capability-bound audit log** — every action a row keyed by capability | Not started |
| 3 | **Capability replay/debugger** — replay an action given a token + workspace and show deny/allow trace | Blocked on #2 |

## Current state

### Latest: PR #40 — `skill:read` / `skill:write` (merged-ready)

- Branch: `claude/review-agent-strategy-qwb2x`
- Head: `ea11e83` (test coverage), preceded by `77fd865` (feat)
- CI: all 10 checks green
- Tests: 1766 passed, 0 failed
- Coverage: 100% on every file the PR touched (682/682 lines)

**What it shipped:**
- `ISkillMemoryActor` (12 methods) takes `CapabilityToken`
- `SkillMemoryActor.Authorize` validates + checks `skill:read` (4 read methods) or `skill:write` (8 write methods); fail-closed via `UnauthorizedAccessException`
- `SkillMemoryActorGrain` forwards token across the Orleans grain boundary
- CQRS records (`StoreSkillCommand`, `GetSkillQuery`, `SearchSkillsQuery`) carry a `CapabilityToken`
- API endpoints (`SkillEndpoints`, `SkillSuggestionEndpoints`) mint per-request tokens via new `SkillTokenFactory`
- Agent-side callers (`AgentSkillSuggester`, `SkillMemoryPromptEnricher`) mint workspace-scoped tokens
- `AgentActor` / `AgentChatPipeline` / `AgentActorGrain` accept `ICapabilityTokenService` via DI
- Strategy doc vocabulary table updated: `skill:write` / `skill:read` row moved Direction → Today

### Capability vocabulary — current status

From [unique-agent-strategy.md](unique-agent-strategy.md) §"Capability vocabulary":

| Grant | Status |
|---|---|
| `tool:<name>`, `tool:*` | Today (`ToolActor.cs`) |
| `secret:<path>`, `secret:*` | Today (`InMemorySecretProvider.cs`) |
| `skill:write` / `skill:read` | **Today** (`SkillMemoryActor.cs`) — landed in PR #40 |
| `channel:send:<channel>` | Direction |
| `channel:receive:<channel>` | Direction |
| `user:read:<userId>` / `user:write:<userId>` | Direction |
| `plugin:invoke:<plugin>` | Direction |
| `marketplace:install` | Direction |

## Next work

### Immediate (item #1 — finish vocabulary expansion)

Pick the next verb. Recommended order (cheapest first):

1. **`channel:send` / `channel:receive`** on `ChannelGatewayActor` and the channel-receive webhooks. Same shape as `skill:*` — interface gains `CapabilityToken`, gateway validates, `ChannelEndpoints` mints, `RouteInboundMessageHandler` carries token. Channel count is small (5) — manageable scope.
2. **`user:read:<userId>` / `user:write:<userId>`** on `UserModelActor`. Per-user wildcard granularity (`user:*:alice`) needs decision: validate exactly OR pattern-match. The strategy doc §"Capability vocabulary" already promises `user:*:alice` works.
3. **`plugin:invoke:<plugin>`** on `DaprToolConnector` / `VaultSecretProvider` / future webhook plugins. Lower priority — plugins aren't directly LLM-reachable today.
4. **`marketplace:install`** when marketplace install path materializes.

### Pre-existing follow-ups surfaced by PR #40 review (out of scope for that PR)

These apply to **both** `ToolActor` and the new `SkillMemoryActor` and should be lifted before vocabulary expansion compounds them:

1. **Cross-workspace token check** — `Authorize` validates signature + grant but doesn't verify `token.WorkspaceId` matches the grain's workspace. A valid token for workspace A authorizes actions on workspace B's actor today. Single-line fix in both `Authorize` (skill) and the `ToolActor`'s inline check.
2. **Manifest-derived grants** — `AgentSkillSuggester` and `SkillMemoryPromptEnricher` mint tokens with hard-coded grants (`["skill:write"]`, `["skill:read"]`). The strategy doc principle 3 says "the workspace manifest's `capabilities` array is the source of truth." Today the runtime mints regardless of manifest. `ToolRegistryConnector` has the same pattern. Fix: route through the agent's `AgentDefinition.Capabilities` before minting.
3. **Denial-path logging** — `Authorize` throws but doesn't log. Strategy doc principle 2 mandates audit emission on every deny. Until item #2 (full audit log) lands, a `LogWarning(...)` per deny path makes operations visible.
4. **`AgentSkillSuggester` null-forgiving operator** — after PR #40's dead-guard removal, the line reads `var skill = AgentSkillExtractor.ExtractFromTask(task, state)!;`. The `!` couples to the caller's precondition implicitly. Either tighten `ExtractFromTask` to return non-null, or add a one-line comment naming the contract. Cosmetic but worth fixing alongside the manifest-grant work.

### Then — item #2 (audit log)

After the vocabulary is broader and the workspace check is in place, the audit log work follows. Concrete starting point: a `CapabilityAuditEvent` record + writer wired into `CapabilityTokenService.Validate` and every `Authorize` call site. The strategy doc's measurement targets become enforceable (100% denial-row coverage; mean time to revoke).

## How to pick this up

1. Read [unique-agent-strategy.md](unique-agent-strategy.md) — the contract.
2. Read [security.md](security.md) — current `CapabilityTokenService` surface.
3. Look at `SkillMemoryActor.cs` + `SkillTokenFactory.cs` (PR #40) as the template to mirror for the next verb.
4. Pick a verb from "Next work" §"Immediate". Open a fresh branch off `main`. Don't reuse `claude/review-agent-strategy-qwb2x` — that's PR #40's branch.

## History

- **2026-05-03** — `skill:read` / `skill:write` landed (PR #40, commits `77fd865` + `ea11e83`). All affected files at 100% coverage. CI green.
- **2026-05-03** (earlier) — Strategy doc and PR #39 reviewed; capability-first principles formalized.
