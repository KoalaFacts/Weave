# Weave — Development Guide

Read [ARCHITECTURE.md](ARCHITECTURE.md) and [docs/best-practices.md](docs/best-practices.md) before changing code. The approved architecture supersedes older project-layout/dependency-layer examples in historical documentation; the security, error-handling, DI, testing and review requirements still apply.

## Build and test

```bash
python3 -m unittest discover -s scripts/tests -v
dotnet restore Weave.slnx
dotnet build Weave.slnx --no-restore -c Release
dotnet test --solution Weave.slnx --no-build -c Release

dotnet test --project tests/Weave.Workspaces.Tests
dotnet test --project tests/Weave.Agents.Tests
dotnet test --project tests/Weave.Security.Tests
dotnet test --project tests/Weave.Tools.Tests
dotnet test --project tests/Weave.Deploy.Tests
dotnet test --project tests/Weave.Silo.Tests
dotnet test --project tests/Weave.Shared.Tests
dotnet test --project tests/Weave.Cli.Tests
```

Use the SDK selected by `global.json`. Most projects target net10.0; `tools/Weave.SourceGen` targets netstandard2.0. Package versions are centralized in Directory.Packages.props except the explicitly opted-out generator project. Regenerate lockfiles after dependency graph changes.

## Layout and composition

- `src/Weave.csproj` is the product library. Its feature folders are directly under `src/`—no Weave/Features wrapper and no mandatory Core/Kernel layers.
- `src/Authority`, `src/Invocations`, `src/Plugins`, `src/Credentials`, `src/Audit`, `src/Workspaces` and other real features own their contracts and behavior.
- `src/Composition` contains existing composition/dispatch collaborators, not every feature's model or service.
- `hosts/Weave.Host/Weave.Host.csproj` is the current Orleans/HTTP executable; its assembly name remains Weave.Silo.
- Other hosts are CLI, Dashboard and Aspire AppHost. Hosts compose; they do not define business policy.
- `extensions/Weave.AgentRuntime` contains the optional current reasoning runtime and model-provider integration; its assembly name remains Weave.Agents.
- MCP, CLI, OpenAPI, Direct HTTP, filesystem and Dapr executors are opt-in extension projects. Provider-specific storage/clustering remains opt-in too.
- Source generators live under `tools/`; tests under `tests/`. Existing test assembly names are retained during structural migration.
- The product assembly is Weave.Product, avoiding the existing `weave` CLI assembly collision. The CLI command remains `weave`.

A feature owns related requests, results, handlers, state, data access and entry-point adapters. Do not create project-wide Models/Services/Controllers/Commands/Actors baskets or duplicate Domain/Application/Infrastructure folders inside every feature. Separate projects need a real packaging, dependency or isolation reason.

Public contracts stay with their owning features. Components use constructor/method injection and explicit registration; ordinary handlers do not receive IServiceProvider as a service locator. Cross-feature callers must not write neighboring state or depend on private handlers.

## Current migration boundaries

This branch first changes code placement/build composition, not authority semantics. Existing namespaces, workspace-bound actor keys and tool-level token behavior remain where explicitly retained. Do not describe that as implemented Room portability or operation-level approval.

Future identity, authorization, persistence or wire changes require their own tests and explicit migration/reset decision. Never silently reset production data, external bindings or audit history. See [implementation record](docs/implementation/2026-09-19-flat-source-foundation.md).

## Rules that remain mandatory

- Warnings are errors. Keep NuGet audit enabled. A diagnostic build with audit warnings is not a passing security gate or release approval.
- No secrets in source, logs, manifests, tool payloads or test fixtures. SecretValue.ToString() remains redacted.
- Keep token validation, context checks, leakage scanning and redaction intact unless a tested replacement is explicitly implemented.
- Fail closed on ambiguous authorization, credential or proof checks. Discovery is not permission.
- Preserve typed failure outcomes; do not swallow exceptions or report failed/unknown operations as success.
- Respect cancellation at async boundaries. Drain redirected stdout and stderr concurrently and bound retained output.
- Pick correct DI lifetimes. Scoped handlers/dispatchers cannot be captured by singletons. Actors receive dependencies through constructors and remain directly testable.
- Use TimeProvider for behavioral time, existing branded IDs internally, and source-generated serialization/registration where supported.
- Pre-1.0 changes update call sites directly: no Legacy classes, dual configuration keys or compatibility shims invented for this refactor.
- Prefer focused classes. A production class over 200 lines needs a design check; do not invent an interface/base class merely to satisfy a template.

## Source generation and actors

Branded ID declarations are physically co-located with owning features. Their current namespaces remain unchanged in the structural increment. Serialization bridges stay with the Orleans host rather than leaking Orleans into the public product protocol.

The product sets `GenerateCqrsRegistration=false` through a compiler-visible property. It still runs branded-ID generation. The executable Host emits the CQRS registry across its complete referenced feature graph; do not accidentally emit duplicate public registries in both assemblies.

Persisted Orleans field IDs are append-only, never renumbered. Namespace/assembly moves are not proof of stored-state compatibility. Native AOT is per-host; some current hosts are excluded and arbitrary runtime DLL loading is not promised.

## Tests and reviews

Use xunit.v3, Shouldly and NSubstitute; do not introduce FluentAssertions or a second .NET test framework. Test naming remains Method_Condition_ExpectedResult, supported by tests/.editorconfig. Python unittest here checks build/layout tooling only.

Add a failing behavior/architecture test before a change, then run the affected tests and full solution as applicable. Shared-assembly boundaries also need namespace/reference checks. Mocks do not prove external side effects, persistence or concurrency.

Run `.claude/skills/check-rules/SKILL.md` and `.claude/skills/adversarial-review/SKILL.md` before source commits and report the reviewed scope. No independent review or successful test run may be claimed without evidence. Separate pre-existing audit failures from regressions introduced by the refactor.

## CLI and defaults

Preserve guided zero-argument mode and explicit scriptable mode. Prompt for missing interactive input; keep helpful defaults and typed failure messages. Retain the command `weave`, the namespaced configuration directory `~/.weave/`, the 94xx local ports and current storage defaults. Never invent default credentials.

## Deployment

Work on feature branches. Do not merge, publish packages, deploy, rewrite production state or force-push main as part of a source-layout task. The target architecture is not itself evidence that a deployment implements its guarantees.
