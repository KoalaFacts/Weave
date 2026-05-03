---
name: check-rules
description: Audit code or a diff against Weave's repo rules in docs/best-practices.md. INVOKE THIS — proactively, without being asked — before claiming any task is done that adds or modifies C#, csproj, manifest, or settings files; when reviewing a branch, PR, or review comment; and after pulling changes you didn't write. Catches the recurring smells codified on this repo: horizontal-cut folders (Models/Services/Helpers/etc.), multi-class files, oversized files, DateTime.UtcNow in behavior logic, Legacy* / dual config keys / deprecated synonyms, third-party packages leaking through abstractions, stale packages.lock.json after a graph change, and List<T>+exact-count assertions on process-global Meter/ActivitySource. Reports violations as a punch list with file:line references and the fix shape — does not auto-fix.
---

# check-rules — Weave repo rule auditor

## When to run

Run this skill, without being asked, before saying "done" on any task that adds or changes C#, csproj, manifest, or settings files. Also run it when reviewing a PR (`gh pr diff`), a branch (`git diff main...HEAD`), or a single commit. It is cheap and the audits are deterministic — there is no "this change is too small to check."

## Source of truth

[`docs/best-practices.md`](../../../docs/best-practices.md) is the law of the repo. If a check below disagrees with that file, the file wins — re-read it and update this skill.

## Audit procedure

Determine the scope first. If running on a diff, get the changed files: `git diff --name-only main...HEAD` (or `gh pr diff <n> --name-only`). If running on a fresh implementation, the scope is whatever you just wrote. If running on the whole repo, scope is `src/`.

Then walk the categories below. Each section names what to check and the exact `grep` / `find` invocation. Report findings as a flat punch list:

```
[severity] file:line — what's wrong → fix shape
```

Severity: `BLOCK` (must fix before merge), `WARN` (fix in this PR or write a follow-up), `NOTE` (mention but don't block).

### 1. Pre-1.0 backward-compat shims (BLOCK)

The repo is pre-1.0; back-compat shims are dead weight. Flag any of:

```bash
# Legacy* constants and properties
grep -rn "Legacy[A-Z]" src --include="*.cs" | grep -v "/bin/\|/obj/"

# Dual config keys (constants with both old and new spellings)
grep -rnE "public const string [A-Za-z]+Backend|public const string [A-Za-z]+Provider" src --include="*.cs" | grep -v "/bin/"
# Then visually scan for synonym pairs (e.g. PostgresBackend + PostgreSqlBackend)

# Fallback reads in FromConfiguration / Get* methods
grep -rnE "\?\?\s*(weaveSection|configuration|cfg)\[" src --include="*.cs" | grep -v "/bin/"

# Switch-case 'or' branches that pair canonical + alias
grep -rnE "case .* or .*:|\"\w+\" or \"\w+\"" src --include="*.cs" | grep -v "/bin/"
```

Existing exceptions (don't flag): switch arms that legitimately handle multiple distinct cases (e.g. `case CommandType.Add or CommandType.Update:` where both are real verbs, not synonyms of one verb). The smell is *synonym* pairs, not multi-case dispatch.

### 2. Per-provider packaging (BLOCK on abstractions; WARN on Silo)

Abstractions projects pull zero third-party provider packages. Each storage/transport provider lives in its own `Weave.X.{Sqlite,Postgres,Redis,...}` opt-in project.

```bash
# Find PackageReferences to provider SDKs in any project
grep -rnE "PackageReference Include=\"(Microsoft\.Data\.Sqlite|Microsoft\.Data\.SqlClient|Npgsql|StackExchange\.Redis|Microsoft\.Orleans\.(Persistence|Clustering)\.(Redis|AdoNet))\"" src --include="*.csproj"

# Verify the abstractions project (e.g. Weave.Security) doesn't pull any
grep -E "PackageReference" src/Security/Weave.Security/Weave.Security.csproj
```

If a domain project (Weave.Security, Weave.Tools, Weave.Agents, Weave.Workspaces, Weave.Deploy) lists any of those packages: BLOCK — extract into a sibling impl project. If `Weave.Silo` lists them directly (as opposed to via a `Weave.Silo.Clustering.X` project): WARN.

### 3. Stale lockfiles (BLOCK if any csproj in the diff)

If the diff touches any `*.csproj`, every consumer's `packages.lock.json` must reflect the new graph.

```bash
# Force regenerate and diff — anything other than zero changes means a stale lockfile slipped in
find src -name "packages.lock.json" -delete
find src -name "obj" -type d -prune -exec rm -rf {} + 2>/dev/null
dotnet restore Weave.slnx --force
git status --short src '*packages.lock.json'
# Any output = lockfiles were stale; commit the regen.
```

This is not optional after a project graph change. The session that shipped the security split missed 225 lines of stale pins because nobody regenerated downstream lockfiles.

### 4. Refactor-as-no-op discipline (BLOCK)

A refactor must not change runtime behavior. When auditing a diff labeled refactor/move/extract/split:

- Read the old and new code side-by-side. Anything in the new code that isn't in the old (extra `?? defaultValue`, new `RegisterFactory` / `Initialize` / `ConfigureAwait` calls, added validation, "while I'm here" cleanups) is a separate concern and should be a separate commit.
- The `git diff` should be ~symmetric: deletions in one place mirror additions elsewhere with identical content modulo namespace/imports.

The Silo clustering split nearly shipped 3 unintended `DbProviderFactories.RegisterFactory` calls disguised as part of the move. That is the canonical cautionary tale.

### 5. File and class size (WARN)

```bash
# Production files over 200 lines
find src -name "*.cs" -not -path "*/bin/*" -not -path "*/obj/*" -not -path "*Test*" \
  | xargs wc -l 2>/dev/null | awk '$1 > 200 {print}' | sort -rn

# Test files over 500 lines
find src -name "*.cs" -not -path "*/bin/*" -not -path "*/obj/*" -path "*Test*" \
  | xargs wc -l 2>/dev/null | awk '$1 > 500 {print}' | sort -rn

# Multiple top-level public/internal types in one file
for f in $(find src -name "*.cs" -not -path "*/bin/*" -not -path "*/obj/*"); do
  n=$(grep -cE "^(public|internal) (sealed |abstract |static )?(partial )?(class|record|interface|struct|enum) " "$f" 2>/dev/null)
  [ "$n" -gt 1 ] && echo "$n  $f"
done | sort -rn
```

For multi-class files: it's only a smell if the types are *not* tightly coupled. The codified exception is the CQRS shape (`*Query` record + `*Handler` class in the same file). Two unrelated public types do not qualify.

### 6. Time and clocks (BLOCK on behavior, NOTE on display)

```bash
grep -rnE "DateTime\.(Now|UtcNow)|DateTimeOffset\.(Now|UtcNow)" src --include="*.cs" \
  | grep -v "/bin/\|/obj/" | grep -vE "Test\.cs|Tests\.cs"
```

For each hit, decide:

- **Property initializer default** (`= DateTimeOffset.UtcNow` on a record field): allowed.
- **Display/log timestamp** (`$"{DateTime.Now:HH:mm:ss}"`, "Refreshed at" label): allowed — not behavior.
- **Anything else** (cache TTL, expiry check, retry backoff, debounce window, hint timeout, mutating actor state): BLOCK — inject `TimeProvider` and call `_timeProvider.GetUtcNow()`. Tests need `FakeTimeProvider`.

Today's known violators (don't re-flag, but call out if new code joins them): `Weave.Agents/Actors/AgentState.cs`, `Weave.Cli/Tui/ChatExitConfirmation.cs`, `Weave.Cli/Commands/Version/VersionService.cs`.

### 7. Vertical slices and code organization (BLOCK on new horizontal cuts)

A new feature should add **one** folder, not entries across many.

```bash
# Pluralized type-name folders that aren't legitimate horizontal cuts
find src -type d -not -path "*/bin/*" -not -path "*/obj/*" \
  | grep -iE "/(Models|Services|Helpers|Utils|Common|Misc|Managers|DTOs)$"

# Top-level folders per project — eyeball for horizontal cuts
for proj in $(find src -name "*.csproj" -not -path "*Test*"); do
  d=$(dirname "$proj"); echo "--- $(basename "$d") ---"; ls "$d" | grep -v -E "\.(csproj|cs)$|^(bin|obj|Properties|Pages|Components|wwwroot)$|packages\.lock\.json|^appsettings"
done
```

Legitimate horizontal cuts (don't flag):
- Composition-root infrastructure: `Startup/`, `Api/`, `Configuration/`, `VirtualActors/`, `Serialization/` in `Weave.Silo`.
- Single-purpose connector folders where each entry IS a feature: `Weave.Tools/Connectors/`, `Weave.Deploy/Translators/`.

Existing violators (call out if the diff makes them worse, e.g. adds another file to `Actors/`): `Weave.Agents/{Actors,Commands,Queries,Events}/`, `Weave.Tools/{Actors,Events}/`, `Weave.Workspaces/{Actors,Commands,Queries,Events}/`, `Weave.Dashboard/Services/`. New code in those folders should land as a new feature folder instead.

### 8. Process-global instruments under tests (BLOCK)

Static `Meter` / `ActivitySource` / `Counter<>` are shared across the whole test assembly; xunit.v3 parallelizes test classes, so concurrent tests will land in any listener's sink.

```bash
# Find MeterListener tests using non-thread-safe sinks
grep -rnE "MeterListener|new MeterListener" src --include="*.cs" -A 20 \
  | grep -B 2 -A 2 "List<.*>\|new List<\|\.Add(" | grep -v "/bin/"

# Find tests asserting exact counts on instrument measurements
grep -rnE "measurements\.Count\.ShouldBe\(\d|\.Count.ShouldBe\(1\)" src --include="*.cs" \
  | grep -v "/bin/"
```

If a test reads from a static instrument and uses `List<T>` instead of `ConcurrentQueue<T>`, or asserts exact counts instead of `ShouldContain`: BLOCK. Pattern lives in `src/Security/Weave.Security.Tests/CapabilityAuthorizerTests.cs`.

### 9. Test discipline (NOTE)

Quick scans for known test smells:

```bash
# FluentAssertions (banned — repo uses Shouldly)
grep -rn "FluentAssertions\|using Fluent" src --include="*.cs" | grep -v "/bin/"

# Tests asserting mock call counts (red flag for testing implementation)
grep -rnE "\.Received\([0-9]+\)\.|\.DidNotReceive\(\)\." src --include="*.cs" | grep -v "/bin/"

# Environment.SetEnvironmentVariable without a try/finally restore
grep -rn "Environment\.SetEnvironmentVariable" src --include="*Test*.cs" | grep -v "/bin/"
# For each: verify there's a finally block that resets it.
```

### 10. Build and test gate (BLOCK)

```bash
dotnet build Weave.slnx 2>&1 | tail -5      # 0 warnings, 0 errors
dotnet test --solution Weave.slnx --no-build 2>&1 | tail -5
```

Warnings are errors in this repo. Tests must be green. Run the full suite at least 3 times if you touched `CapabilityAuthorizer`, any `Meter`-emitting code, or anything Orleans grain-related — flake hides in single runs.

## Output shape

```
== check-rules audit: <scope> ==

BLOCK
  src/Foo/BarActor.cs:42 — DateTime.UtcNow in retry-backoff loop → inject TimeProvider, call _timeProvider.GetUtcNow()
  src/Baz/Quux.csproj:8 — Npgsql in domain project → extract impl into Weave.Baz.Postgres
  src/Foo/Foo.cs — 412 lines, 3 unrelated public types → split per type, each its own file

WARN
  src/Foo/FooTests.cs — 612 lines → production class likely too big; consider splitting

NOTE
  src/Bar/Models/ — folder name 'Models' is the codified smell list; consider co-locating with consumer

If empty: `== check-rules: clean ==`
```

Always also report what you DIDN'T check (e.g. "skipped category 8 — diff has no test changes") so the user can tell whether silence means "looked, found nothing" or "didn't look."
