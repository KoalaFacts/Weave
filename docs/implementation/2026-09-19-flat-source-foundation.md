# Flat Source Foundation — implementation record

## Approved direction

Feature folders are direct children of `src/`, with `src/Weave.csproj`. No `src/Weave/`, `Features/`, or compulsory Core/Application/Kernel/Infrastructure stack. Hosts, extensions, tests and source generators have separate build roots. Contracts remain owned by features. Composition is explicit.

## This increment

This is the first structural increment, not completion of the Agent Control Plane. Existing tool-level token checks, secret handling, runtime behavior, persisted actor keys and public namespaces are deliberately retained. Operation-level grants, Room portability, durable approval/Invocation and generalized resource reconciliation remain subsequent tested changes. No database or external asset is reset.

1. Merge Shared, Workspaces, Security and generic Tools into the feature-organized product library.
2. Move the existing reasoning runtime to `extensions/Weave.AgentRuntime`; model-provider packages are not dependencies of the product library.
3. Extract MCP, CLI, OpenAPI, Direct HTTP, filesystem and Dapr executors into opt-in projects. Their existing public contracts remain in the owning product feature.
4. Move executables into `hosts/`, generators into `tools/`, and test assemblies into `tests/`.
5. Rewrite project references, solution entries, source-generator registration and developer launch paths.
6. Test the resulting project graph, preserve a before/after source hash report, then run .NET build/tests in GitHub Actions.

`src/Weave.csproj` emits `Weave.Product` with root namespace `Weave`. The existing CLI emits `weave`; a different library assembly name avoids a case-insensitive output/dependency collision. Moved existing runtime/host assemblies retain their names. Binary consumers must rebuild; persisted assembly-qualified type compatibility has not been certified.

## Execution and tests

- `scripts/tests/test_source_layout.py` was written first. On the original tree four tests fail for the expected missing flat project/host/extensions and old wrapper directories; two reference-integrity tests pass.
- `scripts/refactor_flat_source.py` builds an exhaustive map before moving files and rejects destination collisions. Only obsolete merged project definitions/lockfiles and redundant global-using declarations are removed.
- After migration, all six structural tests pass in the local environment. Local .NET SDK/network are unavailable; those structural tests are not a substitute for compilation.
- The source map records SHA-256 for every retained original C# file. Namespace and behavior edits are reviewed separately from renames.
- Required commands: `dotnet restore Weave.slnx`, `dotnet build Weave.slnx --no-restore -c Release`, `dotnet test --solution Weave.slnx --no-build -c Release`.

## Known baseline gate

Before source changes, the full-solution restore fails NuGet audit on existing Microsoft.OpenApi, SQLitePCLRaw.lib.e_sqlite3, SSH.NET and MessagePack dependencies. NuGet audit stays enabled. A separately labelled diagnostic build can treat NU1902/NU1903 as warnings to inspect compilation and tests; the strict audit result still fails the workflow and blocks a ready-for-merge claim. This does not approve shipping vulnerable dependencies.

## Review focus

Check transitive connector references, duplicate CQRS registries after assembly merging, serializer visibility, CLI/library output identity, old launch/CI paths, source loss, coverage ownership after merged assemblies and current lockfiles. No automatic merge to main is permitted.

## Remaining before release

Full-solution security audit, lockfile regeneration, coverage accounting for merged assemblies, build/test results and affected namespace/API compatibility must be verified from actual runs. This document records scope and gates, not passing results.
