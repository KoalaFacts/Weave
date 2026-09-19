# Weave — Instructions for Coding Agents

Read [ARCHITECTURE.md](ARCHITECTURE.md), [CLAUDE.md](CLAUDE.md), and the security/testing rules in [docs/best-practices.md](docs/best-practices.md). The approved flat feature architecture supersedes old layer-first layout examples; do not reconstruct the deleted project hierarchy.

## Structure

- Product source is `src/Weave.csproj`, with feature folders directly under `src/`.
- No `src/Weave/`, `src/Features/`, universal Core/Contracts/Kernel projects, or mandatory technical layers.
- Contracts stay with their owning features. Use composition at demonstrated variability boundaries, not a universal class/interface hierarchy.
- Hosts are under `hosts/`; independently selected implementations under `extensions/`; generators under `tools/`; tests under `tests/`.
- The current Host project is `hosts/Weave.Host/Weave.Host.csproj`, emitting the existing `Weave.Silo` assembly.
- The current reasoning runtime is `extensions/Weave.AgentRuntime/Weave.AgentRuntime.csproj`, emitting `Weave.Agents`. A tool-governance consumer does not inherently need this runtime.

## Current behavior versus target design

The initial source migration preserves existing runtime behavior and namespaces. Current Agent actor keys still use `{workspaceId}/{agentName}`; current tool authorization is still tool-level. These are not claims that portable Agent identity, Room-scoped operation grants, durable approvals, or generalized resources have been implemented.

The target in ARCHITECTURE.md separates identity, context and authority. Implement those changes as explicit vertical slices with behavioral tests and a persistence migration decision. Never infer that moving a class changed its semantics.

Historical detailed runtime notes remain in Git history (for example `git show aaa26b600f4b4eaac9b599f8a9f8c2cbc02faf2e:AGENTS.md`); they describe the retained runtime, not the new target ownership model.

## Safety and quality invariants

Preserve token validation, context isolation, fail-closed decisions, credential protection, leakage scanning, redaction, bounded subprocess output and cancellation. Do not create an alternate ungoverned execution route. Do not log secrets or make plugin installation imply permission.

In-process code is trusted, not sandboxed by an interface or assembly loader. Out-of-process execution is not isolated unless its credentials, network, process and filesystem permissions are actually constrained.

Tests use xunit.v3, Shouldly and NSubstitute. Test layout conventions live in `tests/.editorconfig`. Keep warnings-as-errors and NuGet audit. Do not suppress vulnerabilities to claim a passing release gate.

Run before completion:

```bash
python3 -m unittest discover -s scripts/tests -v
dotnet restore Weave.slnx
dotnet build Weave.slnx --no-restore -c Release
dotnet test --solution Weave.slnx --no-build -c Release
```

Run `.claude/skills/check-rules/SKILL.md` and `.claude/skills/adversarial-review/SKILL.md` for source changes. Report actual evidence and any test/build/audit failure. A diagnostic compile is not security approval.

## Working policy

Do not reset persisted state, delete upstream resources, change public protocols, merge to main, publish packages or deploy as a side effect of refactoring. Rebuild binary consumers after assembly changes and explicitly assess stored-state compatibility.

No backward-compatibility shims merely for pre-1.0 cleanup. Update actual call sites and keep one authoritative definition per semantic identity. No service locator in ordinary handlers, blanket repository pattern, or fake placeholder implementation described as finished.

The current increment and known verification limits are recorded in [the implementation record](docs/implementation/2026-09-19-flat-source-foundation.md).
