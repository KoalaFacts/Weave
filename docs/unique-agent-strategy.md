# Unique Agent Strategy

> **Status**: Strategic direction. Supersedes the *Recommendations* sections of [competitive-analysis.md](competitive-analysis.md) for roadmap purposes. The competitor profiles, feature matrix, and source links in that doc remain valid as market context.
> **See also**: [security.md](security.md), [manifest-reference.md](manifest-reference.md), [best-practices.md](best-practices.md)

## Why this doc exists

`docs/competitive-analysis.md` reads as a catch-up plan: skill memory like Hermes, channel breadth like OpenClaw, user modeling like Honcho, a marketplace to answer ClawHub. Several of those features are already shipped and advertised in [README.md](../README.md) — they are real, they will keep working, and they will keep getting maintenance fixes. None of them is the reason a serious adopter would pick Weave over a Python framework with a four-year head start.

The reason is the security model. Weave is the only project in this comparison where every action an agent takes flows through a signed, scoped, revocable capability check at a grain boundary the LLM cannot reach. That is the focus. Everything else is a feature; capability-first is the architecture.

This doc names that focus, names what we will and will not compete on, and reorders the roadmap accordingly.

## Positioning

> **Weave is the capability-first agent runtime.** Every tool call, secret read, channel send, plugin invocation, and skill write is a signed, scoped, time-boxed, revocable capability. The runtime fails closed by default.

Two sentences. The rest of this document defends them.

## The five principles

### 1. Every action is a capability

The capability model is not a tool-permission system. It is the action model. `IToolConnector.ConnectAsync` and `IToolActor.InvokeAsync` already take a `CapabilityToken` parameter ([tools.md:21](tools.md), [tools.md:90](tools.md), [tools.md:92](tools.md)) — the same shape extends to memory writes, channel sends, plugin calls, and marketplace installs. New capability strings (`channel:send`, `skill:write`, `user:read`, `plugin:invoke`) join the vocabulary instead of new permission systems being invented next to it.

There is one ACL surface, one token type, one validator. Hermes has none. OpenClaw's CVE history is what happens without one.

### 2. Fail closed, log loud

Ambiguous capability validation denies. Ambiguous secret scanning denies. Ambiguous proof verification denies. This is already the rule of the repo ([best-practices.md:29](best-practices.md)). Every denial emits an audit event keyed by the capability that was checked, the grant that was missing, and the actor that requested it.

Capability-first is not "we have tokens." It is "the token's grant is the only thing that authorizes the action, and if we are unsure which token applies, we deny." A `catch (NullReferenceException)` that swallows a missing workspace identity, a default-to-wildcard on a missing token, a "trust the caller" path past validation — all of those are the same bug, and `best-practices.md` exists to keep them out.

### 3. Capabilities are declared, not inferred

The workspace manifest's `capabilities` array on each agent ([manifest-reference.md:111](manifest-reference.md), [manifest-reference.md:131](manifest-reference.md)) is the source of truth. The runtime never elevates an agent silently; the LLM cannot ask for more than the manifest grants; an agent that wants a new capability requires a manifest edit, which is a code review, which is a human in the loop.

This is the property regulated environments need: the manifest *is* the access policy, version-controlled, diffable, reviewable. There is no out-of-band grant mechanism.

### 4. Scoped, signed, time-boxed, revocable

`CapabilityTokenService` mints HMAC-SHA256 tokens with workspace, issuer, grants, issued-at, and expires-at, defaulting to a 24-hour lifetime; verifies signatures with constant-time comparison; supports wildcard grants and file-backed revocation ([CapabilityTokenService.cs:36](../src/Security/Weave.Security/Tokens/CapabilityTokenService.cs), [:55](../src/Security/Weave.Security/Tokens/CapabilityTokenService.cs), [:69](../src/Security/Weave.Security/Tokens/CapabilityTokenService.cs), [:84](../src/Security/Weave.Security/Tokens/CapabilityTokenService.cs); full surface in [security.md:15-62](security.md)).

Every adjective in the positioning sentence maps to a behavior already in the code. We are not promising a model; we are naming the one that exists and committing to extend it instead of bypassing it.

### 5. Boundaries cross at the grain

Capability checks live at the Orleans grain boundary, not in agent prompt instructions. The LLM cannot talk its way past them. A jailbroken system prompt cannot fabricate a `CapabilityToken` whose signature validates. A poisoned tool response gets scanned by the leak scanner ([security.md:64+](security.md)) before it reaches the agent's context — and the scanner is itself a capability-bound service, not a string filter the model can argue with.

Hermes relies on container isolation; that is one boundary, far from the action. Weave's boundary is the actor call itself.

## What we compete on

- **Capability vocabulary and grant model.** Adding a new capability string is the canonical way to introduce a new kind of action.
- **Leak scanning + secret proxy as capability-bound services**, not bolt-ons.
- **Auditable, replayable action log keyed by capability.** Every action is a row.
- **Orleans grain boundaries as the enforcement seam** — distributed, recoverable, individually revocable.
- **Manifest-as-permission-policy.** The workspace JSONC is the agent's ACL. Diffable in PR review.

## What we explicitly do not compete on

These are real Weave features. They will keep working. They are not where we invest, market, or measure ourselves.

- **Channel breadth.** We have five — Slack, Discord, Telegram, Microsoft Teams, Email ([README.md:151](../README.md)). We are not chasing OpenClaw's 50+ or Hermes's 15+. A sixth channel is acceptable when it is a customer-funded ask, not a roadmap item.
- **Community skill marketplace volume.** Curated only, every entry capability-reviewed. ClawHub's 44,000 community skills is a number, not a goal.
- **User-modeling depth.** We track only what is needed to scope capabilities to a user. No personality profiles, no Honcho-equivalent.
- **Self-improving skill memory as a flagship feature.** The `SkillMemoryActor` exists and works. It is not the headline. Skill writes mint capabilities like everything else.
- **Python ecosystem reach.** Interop via MCP. No native Python story, no parallel Python SDK.

If a feature request lands and the answer is "this would close a gap with Hermes/OpenClaw" — that is not, by itself, a reason to build it. The reason has to route through a capability we want to model.

## Roadmap implications

Concrete reprioritization against the existing [competitive-analysis.md](competitive-analysis.md) "Priority roadmap to close gaps" list:

| Item | Old position | New position | Why |
|---|---|---|---|
| Skill memory system | #1 | maintain only | Already exists, reframed as a capability consumer, not a flagship. |
| Channel gateway expansion | #2 | not pursued | Five is enough; gap-closing for its own sake is rejected. |
| User-modeling actor | #3 | scope to capability targeting | Cross-session profile depth is not the moat. |
| Curated marketplace | #4 | maintain | Aligned — curation *is* capability review. |
| Capability templates | #5 | **promoted** | They already are pre-validated capability bundles. Lean in. |
| **Capability vocabulary expansion** | (new) | **#1** | `channel:send`, `skill:write`, `user:read`, `plugin:invoke`. |
| **Capability-bound audit log** | (new) | **#2** | Every action a row keyed by capability. Replayable. |
| **Capability replay/debugger** | (new) | **#3** | Given a token and a workspace, replay the action and show the deny/allow trace. |

## How this differs concretely

**vs. Hermes Agent.** Hermes's three-layer memory (session, persistent, skill) has no permission model: skill writes are unscoped, persistent memory is unscoped, the LLM decides what to remember. In Weave a skill write is an action, an action requires a capability, the capability is signed and revocable. Same memory model, different ground rules.

**vs. OpenClaw.** OpenClaw is the case study for why capability-first matters. CVE-2026-25253 (one-click RCE), nine CVEs in four days, 12-20% of community skills flagged malicious — these are not implementation bugs. They are what happens when an agent platform has no capability boundary the LLM cannot cross. We do not need to argue this section; we need to point at it.

**vs. Evlover.** Evlover's GEP gene fragments are unsigned and inheritable across the network. Weave's nearest analogue would be a shared capability bundle: signed, scoped, capability-reviewed, and instantiated through the existing capability-template mechanism. Same idea, different trust model.

## Capability vocabulary

The grant strings in `CapabilityToken.Grants` are the runtime's user interface for permission. Today the vocabulary is tool-shaped (`tool:git`, `tool:files`, `tool:*`). The direction is to extend it along the same shape so every action — not just tools — is a verb in the same language.

| Grant pattern | Action it gates | Today / Direction |
|---|---|---|
| `tool:<name>` | Connector invoke and connect | Today |
| `tool:*` | Any tool | Today |
| `secret:<path>` | Secret proxy resolve | Today (see [security.md:155-171](security.md)) |
| `channel:send:<channel>` / `channel:receive:<channel>` | Channel routing (inbound + reply) | Today (enforced at [ChannelGatewayActor.cs](../src/Assistants/Weave.Agents/Actors/ChannelGatewayActor.cs)) |
| `skill:write` / `skill:read` | Skill-memory persistence | Today (enforced at [SkillMemoryActor.cs](../src/Assistants/Weave.Agents/Actors/SkillMemoryActor.cs)) |
| `user:read:<userId>` / `user:write:<userId>` | User-profile access | Today |
| `plugin:invoke:<plugin>` | Hot-swap plugin connect/disconnect (Dapr, Vault, webhook) | Today (enforced at [PluginRegistry.cs](../src/Runtime/Weave.Silo/Plugins/PluginRegistry.cs)) |
| `marketplace:install` | Installing a marketplace item | Direction |

Wildcards live in [`CapabilityToken.HasGrant`](../src/Security/Weave.Security/Tokens/CapabilityToken.cs) and match segment-wise: each `*` covers one segment, except a trailing `*` which covers one or more. `tool:*` matches any depth under `tool`; `user:*:alice` matches both `user:read:alice` and `user:write:alice`; the bare `*` matches anything. Manifest-side wildcards (when the manifest declares `skill:*` instead of `skill:read`) are still ahead of us — runtime checks against the manifest use direct `.Contains(grant)` today.

## Decision rules for PR review

A reviewer reading a change against this doc applies three questions, in order:

1. **Does the change introduce a new kind of action?** A new connector type, a new memory store, a new outbound integration. If yes, the change must add a grant string and a validation site, or extend an existing one. A new action without a grant is the rejection criterion.
2. **Does the change loosen a fail-closed default?** Catching an exception that used to deny, defaulting a missing token to a wildcard, treating `null` as "any workspace." If yes, the change is rejected unless the PR description names the threat model that allows it.
3. **Does the change route a security-relevant decision through the LLM?** Asking the model to produce a token, asking the model to authorize a tool, asking the model to decide whether a scan result is a leak. If yes, the change is rejected — the LLM is on the other side of the boundary.

Anything else — channel additions, skill-memory tweaks, user-profile expansions — is a normal feature change reviewed on normal feature criteria.

## Measurement

The metrics that define success under this strategy are not the metrics that define success under a feature-race strategy.

- **Time from manifest grant to enforced denial** — how fast a removed capability becomes an actual deny at the grain. Target: single-digit seconds, bounded by token TTL or revocation list propagation.
- **Coverage of action types by capability check** — percentage of action-emitting code paths that mint or validate a token. Target: 100%, with gaps tracked as bugs.
- **Audit completeness** — percentage of denied actions that produced an audit row with capability, grant, actor, and reason. Target: 100%.
- **Mean time to revoke a leaked token** — from incident detection to no-further-action-with-this-token. Target: minutes, bounded by the revocation cache.

Channel count, skill count, marketplace size, GitHub stars, contributor count — all real, none of them on this list. They are vanity metrics for the strategy this doc replaces.

## Risks and limits

This strategy can be wrong. The honest cases:

- **Capability-first does not market itself.** "Self-improving" and "talks on Slack" demo well; "every action is a signed capability" does not, until the room contains a security or compliance buyer. We accept a slower top-of-funnel in exchange for a defensible bottom.
- **The LLM-jailbreak frontier is moving.** Capability-first defends the boundary the LLM tries to cross; it does not solve prompt injection that operates *within* the agent's granted capabilities. A capability over-grant in the manifest is a human bug we cannot catch at runtime.
- **Vocabulary drift.** If `channel:send:*` and `tool:slack` both exist as ways to authorize sending a Slack message, the model is incoherent. The doc commits us to one verb per action class; review must enforce it.
- **Token-revocation latency.** "Time from grant removal to enforced denial" is bounded by token TTL and revocation propagation. A 24-hour default is a 24-hour worst case for a stale token. Shortening the default is a tradeoff against churn.
- **Under-investment surfaces a competitor's strength.** If a serious customer asks for a sixth channel or deeper user modeling, the answer is not "no, read the strategy doc." The answer is "yes, here is how we model it as a capability." This doc reorders the roadmap; it does not freeze it.

## What this means for contributors

If you are adding a new feature: ask first which capability it gates. If the answer is "none," the design is incomplete. Capabilities are not added at the end of a change as a security pass; they are the first design decision.

If you are adding a new action class: the grant string lands in this doc's vocabulary table in the same PR. The doc is the contract, not the implementation's afterthought.

If you are reviewing a change: apply the three decision rules above. Cite this doc when rejecting.

If you are updating this doc: edits that loosen a principle, add a "do not compete on" exception, or change the vocabulary table require the same review weight as a capability-token change in code. The doc is the contract; tightening it is a normal PR, loosening it is a strategy decision.

## Non-goals for this doc

- Does not redesign the token format.
- Does not change the manifest schema.
- Does not promise new features. Every code path cited above already exists.
- Does not deprecate skill memory, channels, user modeling, or the marketplace as features. It deprecates them as marketing pillars and as roadmap-priority anchors.
- Does not replace [competitive-analysis.md](competitive-analysis.md). It supersedes the *Recommendations* sections of that doc; the analysis itself stays.
