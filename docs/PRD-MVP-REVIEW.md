# Weave MVP PRD — Implementation Review

**Date:** 2026-03-13
**Scope:** Review of `docs/PRD-MVP.md` against the current codebase

---

## Overall Assessment

The codebase is well-aligned with the PRD. All five MVP goals have working implementations, and the architectural conventions (Orleans grains, CQRS, branded IDs, lifecycle hooks) are applied consistently. The main gaps are in validation depth, CLI coverage for agent operations, and dashboard interactivity — none of which block the stated happy path.

---

## Goal-by-Goal Coverage

### Goal 1 — Create and validate a workspace manifest from the CLI

| Requirement | Status | Notes |
|---|---|---|
| `workspace.yml` version 1.0 | Done | ManifestParser enforces `"1.0"` |
| Workspace, agent, tool, target, hook sections | Done | All sections modeled and parsed via YamlDotNet |
| Required-field validation | Done | Name, agent model, tool type, target runtime checked |
| Cross-reference validation (agent → tool) | Done | Agent tool refs verified against manifest tools |
| `weave workspace new` | Done | Scaffolds manifest, prompts/, data/, .weave/ |
| `weave config validate` | Done | Reports errors or success |

**Gaps:**
- No validation of system prompt file paths, cron syntax, network subnet format, or vault config when provider is `vault`. These are nice-to-haves; the PRD only asks for "required fields and basic cross-references."
- No model-name validation (any string accepted). Acceptable for MVP since model availability depends on the provider.

**Verdict: Meets PRD requirements.**

---

### Goal 2 — Start/stop a workspace and observe state from CLI and dashboard

| Requirement | Status | Notes |
|---|---|---|
| HTTP start/stop endpoints | Done | `POST /api/workspaces`, `DELETE /api/workspaces/{id}` |
| Persist state via Orleans grains | Done | `WorkspaceGrain` with `IPersistentState<WorkspaceState>` |
| Provision local runtime (Podman) | Done | `PodmanRuntime` creates network + MCP tool containers |
| `weave up` / `weave down` | Done | Reads manifest, calls API, stores workspace-id in `.weave/` |
| `weave status` | Done | Queries live API or falls back to manifest data |
| Dashboard workspace view | Done | `/workspaces` page with status, container count, timestamps |
| Active workspace registry | Done | Singleton `WorkspaceRegistryGrain` tracks active IDs |

**Gaps:**
- Only Podman is implemented. PRD mentions "current `PodmanRuntime`" so this is expected, but the open question about Docker support is unresolved.
- Dashboard workspace page is read-only — no inline start/stop actions. Acceptable since the PRD positions CLI as the primary interface.
- No drill-down from workspace list to workspace detail in the dashboard.

**Verdict: Meets PRD requirements. Docker runtime is a documented open question.**

---

### Goal 3 — Activate agents, register tools, expose status via HTTP

| Requirement | Status | Notes |
|---|---|---|
| Activate agents from manifest | Done | `AgentSupervisorGrain.ActivateAllAsync` |
| Track agent status, tasks, history, tools | Done | `AgentState` with status enum, `ActiveTasks`, `History`, `ConnectedTools` |
| Agent HTTP endpoints | Done | List, get, activate, deactivate, message, task submit/complete |
| Chat and task submission via API | Done | `POST .../messages`, `POST .../tasks` |
| Register tools per workspace | Done | `ToolRegistryGrain` with connection tracking and access control |
| Resolve tools for agents | Done | `ResolveAsync(agentName, toolName)` with per-agent allowlists |
| MCP, CLI, OpenAPI, Dapr connectors | Done | All four connector types implemented |
| Tool HTTP endpoints | Done | `GET .../tools`, `GET .../tools/{name}` |

**Gaps:**
- MCP schema discovery returns a placeholder — no actual JSON-RPC `tools/list` call. Tools still connect and invoke correctly; schema is cosmetic for MVP.
- No CLI commands for agent activation, chat, or task submission. Users must use the API or dashboard. The PRD lists "should agent chat and task submission get first-class CLI commands" as an open question, so this is expected.
- Dashboard chat interface works but has no task-submission UI. Agent chat is functional through the `/agents` page.

**Verdict: Meets PRD requirements. CLI agent commands are a documented open question.**

---

### Goal 4 — Basic security controls for tool access and secret leakage

| Requirement | Status | Notes |
|---|---|---|
| Capability tokens for tool access | Done | HMAC-SHA256 signed tokens with grants, expiry, revocation |
| Scan input/output for secret leakage | Done | 15 named regex patterns + Shannon entropy analysis |
| Secret substitution via proxy | Done | `TransparentSecretProxy` with `{secret:path}` placeholders |
| Fail closed on token or scan failure | Done | Invalid tokens throw; leaked responses are redacted; blocked invocations publish events |
| Secret providers | Done | InMemory + HashiCorp Vault via VaultSharp |

**Gaps:**
- No audit logging of secret access (only events published). Sufficient for MVP.
- No rate limiting on token minting. Not required by PRD.
- Revocation stored in temp directory — not suitable for distributed production, but fine for local MVP.

**Verdict: Meets PRD requirements. Security posture is strong.**

---

### Goal 5 — Generate deployment artifacts from the same manifest

| Requirement | Status | Notes |
|---|---|---|
| docker-compose | Done | Silo + Redis + tool containers, bridge network |
| kubernetes | Done | Namespace, Deployment, StatefulSet (Redis), Service |
| nomad | Done | HCL job with silo + redis groups, Dapr sidecar |
| fly-io | Done | fly.toml with auto-scaling, health checks, HTTPS |
| github-actions | Done | Workflow with Redis service container, per-agent steps |
| `weave publish <target>` | Done | Supports all 5 targets + aliases (k8s, fly, gh-actions) |
| Output to user-selected folder | Done | `--output` flag, defaults to `./output` |

**Gaps:**
- `PublishOptions.Variables` dictionary is defined but unused by any publisher. Template variable substitution is not wired. Not required by PRD.
- Generated Kubernetes manifests lack RBAC, PVC, and resource limits. Acceptable for MVP artifact generation.
- No secret injection patterns in generated artifacts (env vars are hardcoded placeholders).

**Verdict: Meets PRD requirements.**

---

## Non-Functional Requirements

| Requirement | Status |
|---|---|
| Target .NET 10 for runtime projects | Done |
| SourceGen on netstandard2.0 | Done |
| Warnings as errors | Done (Directory.Build.props) |
| Test coverage in existing test projects | Done — Agents, Tools, Security, Deploy, Workspaces all have tests |
| Source-generated / trimming-aware patterns | Done — `[GenerateSerializer]`, `[GeneratedRegex]`, AOT flags |

---

## Happy Path Walkthrough

| Step | CLI Command | Status |
|---|---|---|
| 1 | Start `Weave.AppHost` | Done (Aspire orchestration) |
| 2 | `weave workspace new demo` | Done |
| 3 | Edit `workspace.yml` | Manual (expected) |
| 4 | `weave config validate --workspace demo` | Done |
| 5 | `weave up --workspace demo` | Done |
| 6 | Silo provisions workspace, activates agents, connects tools | Done |
| 7 | `weave status --workspace demo` or dashboard | Done |
| 8 | `weave publish kubernetes --workspace demo` | Done |
| 9 | `weave down --workspace demo` | Done |

**The end-to-end happy path is fully implemented.**

---

## Open Questions (PRD) — Current Answers

| Question | Current State |
|---|---|
| Should MVP require Podman or also support Docker? | Only Podman implemented. `IWorkspaceRuntime` is abstracted, so Docker could be added without restructuring. Recommend documenting Podman as the MVP requirement. |
| What sample provider config for `IChatClient`? | `AgentChatClientFactory` wraps an injected `IChatClient`. Silo registers it, but no default provider sample ships in the scaffold. Recommend adding a comment or env-var placeholder in the generated `workspace.yml`. |
| Should agent chat/task get CLI commands? | Not implemented. API and dashboard cover it. Recommend deferring to post-MVP unless the 15-minute onboarding metric requires it. |
| Which dashboard pages are MVP vs post-MVP? | Home, Workspaces, Agents (with chat), Tools are implemented. Setup wizard UI exists but has no backend wiring. Recommend shipping current pages and treating Setup as post-MVP. |

---

## Risks (PRD) — Assessment

| Risk | Assessment |
|---|---|
| Podman narrows environment assumptions | Confirmed. Single runtime. Mitigated by `IWorkspaceRuntime` abstraction. |
| Manifest concepts broader than runtime | Confirmed. `Replicas`, `Trigger`, `Region`, `Scaling` fields are parsed but only used in deploy publishers, not in local runtime. Acceptable. |
| Dapr support is conditional | Confirmed. `DaprToolConnector` exists but requires Dapr sidecar. Silo has `DaprEventBus` with in-process fallback. |
| Dashboard lags behind API | Confirmed. Dashboard is read-mostly. No task submission, no workspace actions, no cost display, no monitoring page. Chat works. |

---

## Recommendations

### Ship as-is for MVP
1. The happy path works end-to-end
2. Security controls are production-grade
3. All five deployment targets generate artifacts
4. Test coverage is solid across all modules

### Address before public preview
1. **Docker runtime** — add a `DockerRuntime` implementing `IWorkspaceRuntime` to widen the audience
2. **CLI agent commands** — `weave agent chat` and `weave agent task` would close the "no API calls needed" success metric
3. **Dashboard workspace detail** — drill-down from workspace list to see agents/tools/state in one view
4. **Setup wizard backend** — wire the existing UI or remove the dead page
5. **IChatClient sample** — ship a default provider config (e.g., Ollama or Anthropic) in the scaffold so first-run works without source inspection

### Post-MVP enhancements
- MCP schema discovery (actual `tools/list` JSON-RPC)
- Persistent task history / audit log
- Cost tracking surfaced in dashboard and CLI
- Template variable substitution in deploy publishers
- Advanced cron parsing for heartbeats
- Hot-reload of tool configuration
