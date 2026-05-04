---
name: check-rules
description: Audit code or a diff against Weave's repo rules in docs/best-practices.md. INVOKE THIS — proactively, without being asked — before claiming any task is done that adds or modifies C#, csproj, manifest, or settings files; when reviewing a branch, PR, or review comment; and after pulling changes you didn't write. Catches the recurring smells codified on this repo: horizontal-cut folders, multi-class files, oversized files, DateTime.UtcNow in behavior logic, Legacy* / dual config keys / deprecated synonyms, third-party packages leaking through abstractions, stale packages.lock.json after a graph change, List<T>+exact-count assertions on process-global Meter/ActivitySource, swallowed/verbose catch clauses, static-class service-locator patterns, over-verbose XML doc / narrating comments, reflection where source-gen is available, dead/orphan code after refactors, and verify-nothing tests. Reports violations as a punch list with file:line references and the fix shape — does not auto-fix.
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

### 9. Catch-clause hygiene (BLOCK on swallowed catches; WARN on verbose form)

```bash
# Bare catch / catch (Exception) without a `when` filter
grep -rnE "catch\s*\{|catch\s*\(\s*Exception\s+\w+\s*\)\s*\{" src --include="*.cs" \
  | grep -v "/bin/\|/obj/" | grep -vE "Test\.cs|Tests\.cs"

# Swallowed catch — empty body or only a /* ignore */ comment
grep -rnE "catch.*\{\s*(/\*[^*]*\*/\s*)?\}" src --include="*.cs" \
  | grep -v "/bin/\|/obj/"

# Verbose form for a single type: catch (Exception ex) when (ex is X)
grep -rnE "catch\s*\(\s*Exception\s+\w+\s*\)\s*when\s*\(\s*\w+\s+is\s+\w+\s*\)" src --include="*.cs" \
  | grep -v "/bin/"

# Catch with named bind variable that's never used in the body
# (rough heuristic — verify by reading)
grep -rnE "catch\s*\(\s*\w+(Exception)\s+(ex|e)\s*\).*\{\s*/\*" src --include="*.cs" \
  | grep -v "/bin/"
```

For each:
- Empty/swallowed: BLOCK unless the body has a real reason (terminal capability probe etc.). At minimum log at `Debug`.
- Verbose form for a single type → WARN: simplify to `catch (X)`.
- Named bind variable never read → WARN: drop the variable, use `catch (X)`.

### 10. Static helpers vs DI (WARN)

```bash
# Static classes whose methods take IServiceProvider — service locator smell
grep -rn "public static.*IServiceProvider\|internal static.*IServiceProvider" src --include="*.cs" \
  | grep -v "/bin/" | grep -vE "Test\.cs|Tests\.cs"

# Static class with mutable state (static field that's not const/readonly Meter/etc.)
grep -rnE "(public|internal) static class" src --include="*.cs" -A 5 \
  | grep -B 2 "static \w+ \w+\s*=\|static \w+\? \w+;" | grep -v "/bin/"
```

Static is acceptable for: constants, pure functions, extension methods, source-generator output, `System.CommandLine` command-tree builders. Static is wrong when methods take `IServiceProvider` to do `GetRequiredService<>` (register the class, take deps via ctor) or when the class holds mutable state.

### 11. Comment discipline (NOTE)

```bash
# XML doc summary blocks > 5 lines
for f in $(find src -name "*.cs" -not -path "*/bin/*" -not -path "*/obj/*" | grep -v Test); do
  longest=$(awk '/^\s*\/\/\//{c++; if (c>m) m=c} !/^\s*\/\/\//{c=0} END{print m+0}' "$f")
  [ "$longest" -gt 5 ] && echo "$longest  $f"
done | sort -rn | head

# Comments that narrate (Get/Set/Returns/Adds/Loops)
grep -rnE "^\s*//\s+(Get|Set|Return|Add|Create|Initialize|Construct|Build|Loop|Iterate) " src --include="*.cs" \
  | grep -v "/bin/" | grep -vE "Test\.cs|Tests\.cs"

# Comments referencing tasks/PRs/dates that will rot
grep -rnE "^\s*//.*(TODO|FIXME|XXX|added for|fixes #|see PR|as of [0-9]{4})" src --include="*.cs" \
  | grep -v "/bin/" | grep -vE "Test\.cs|Tests\.cs"
```

For each finding:
- 5+ line `<summary>`: WARN — split the type, or move detail to `<remarks>`. The `<remarks>` form is fine when documenting non-obvious context (startup ordering, threading, lifetime quirks).
- Narrating comments: NOTE — delete; rename the symbol if needed.
- Task/PR/date references: NOTE — that context belongs in the PR description, not the code. Exception: external bug links (`// Workaround for dotnet/runtime#12345`) are durable.

### 12. Source generation over reflection (BLOCK on new reflection paths)

```bash
# Anonymous-type JSON serialization (cannot be added to a JsonSerializerContext)
grep -rnE "JsonSerializer\.(Serialize|SerializeToUtf8Bytes)\(\s*new\s*\{" src --include="*.cs" \
  | grep -v "/bin/\|/obj/" | grep -vE "Test\.cs|Tests\.cs"

# JsonSerializer.Serialize<T>(value) without a passed JsonTypeInfo / context
grep -rnE "JsonSerializer\.(Serialize|Deserialize)<\w+>\([^)]*\)" src --include="*.cs" \
  | grep -v "/bin/\|/obj/" | grep -vE "JsonContext|JsonTypeInfo|Test\.cs|Tests\.cs"

# Raw `new Regex("...")` in production
grep -rnE "new Regex\(" src --include="*.cs" | grep -v "/bin/" | grep -vE "Test\.cs|Tests\.cs"

# Reflection-based DI scanning
grep -rnE "assembly\.GetTypes\(\)|Assembly\.GetExecutingAssembly\(\)\.GetTypes" src --include="*.cs" \
  | grep -v "/bin/" | grep -vE "Test\.cs|Tests\.cs"

# RequiresUnreferencedCode in production code (has-to-go list)
grep -rn "\[RequiresUnreferencedCode" src --include="*.cs" \
  | grep -v "/bin/" | grep -vE "Test\.cs|Tests\.cs"
```

If a feature has a source generator (STJ, `[GeneratedRegex]`, `[LoggerMessage]`, the repo's `BrandedIdGenerator` / `CqrsRegistrationGenerator`), use it. Anonymous types in `JsonSerializer.Serialize` are the most common smell — they bypass any registered context. Fix shape: define a `record FooPayload(...)` and add `[JsonSerializable(typeof(FooPayload))]` to the relevant context. Reflection-based DI registration is dead code in this repo (production uses source-gen) — delete on sight per the no-back-compat rule.

### 13. Dead / orphan code (BLOCK on obviously orphaned types after a refactor)

After any deletion or contract change, search for symbols whose only caller was what you just removed. Look at the diff first — every `-` removal is a candidate to leave something orphaned.

```bash
# Anything still mentioning Legacy* (after the legacy-key removal we did)
grep -rn "Legacy" src --include="*.cs" | grep -v "/bin/\|/obj/"

# RequiresUnreferencedCode in production = reflection path that should have a
# source-gen replacement, OR is dead. Both are red flags after a source-gen migration.
grep -rn "\[RequiresUnreferencedCode" src --include="*.cs" | grep -v "/bin/" | grep -vE "Test\.cs|Tests\.cs"

# Internal types with zero refs outside their defining file
for f in $(find src -name "*.cs" -not -path "*/bin/*" -not -path "*/obj/*" -not -path "*Test*"); do
  for sym in $(grep -oE "^internal (sealed |abstract |static |partial )*(class|record|interface) \w+" "$f" | awk '{print $NF}' | sort -u); do
    count=$(grep -rE "\b${sym}\b" src --include="*.cs" 2>/dev/null | grep -v "/bin/\|/obj/" | grep -v "$f:" | wc -l)
    [ "$count" -eq 0 ] && echo "$f -> $sym (zero external refs)"
  done
done
```

False positives to verify before deleting (these don't show up in `grep -r`):
- `System.CommandLine` command builders are referenced via the `Command` tree wired in `Program.cs`.
- CQRS handlers are wired by source-generated `AddGeneratedCqrsHandlers()` (output in `obj/`).
- Razor event handlers are called from `@onclick="@MethodName"` in the matching `.razor` file (not the `.razor.cs`). Add `--include="*.razor"` to the grep.
- Orleans grain bridges are resolved by the cluster client via `IGrainWithStringKey` keys, not direct refs.
- `[JsonSerializable]`-attributed types are dispatched through the context at runtime.

When auditing a diff: any deletion (`git diff --diff-filter=D`) is a trigger to scan whether the removed callers leave anything orphaned downstream.

### 14. Meaningful tests (BLOCK on verify-nothing patterns)

```bash
# Tests whose only assertion is ShouldNotBeNull / NotThrow — likely verify-nothing
grep -rnE "ShouldNotBeNull|NotThrowAsync" src --include="*Test*.cs" \
  | grep -v "/bin/" | head

# For each candidate test method, count its Should* assertions:
#   1 assertion AND that assertion is ShouldNotBeNull/NotThrow → verify-nothing
#   review the test body manually

# Round-trip tests on records — compiler guarantees this
grep -rnE "new \w+ \{[^}]+\}\.\w+\.ShouldBe" src --include="*Test*.cs" | grep -v "/bin/" | head

# Mock-heavy tests asserting a stubbed return value (passes nothing through SUT)
grep -rnE "\.Returns\(.*\);.*ShouldBe\(" src --include="*Test*.cs" | grep -v "/bin/" | head

# .Received(N) assertions — locking implementation, not behaviour
grep -rnE "\.Received\(\d+\)\.|\.Received\(\)\." src --include="*Test*.cs" | grep -v "/bin/" | head

# Theory rows that don't branch (heuristic — find Theories with > 5 rows on a method
# that has no value-dependent branching; manual review)
grep -rcE "\[InlineData" src --include="*Test*.cs" | grep -v ":0$" | sort -t: -k2 -rn | head
```

For each candidate, ask:
- "What bug would this test catch?" If "the SUT throws an NRE" is the only answer, the test is decoration.
- "If I rewrote the SUT to `return default`, would the assertions fire?" If no, the test verifies nothing.
- For `.Received(N)`: is the count meaningful (you're asserting "called exactly once" because more would be a bug), or is it "I happen to know my impl calls it 3 times"? Latter → behavioural assertion instead.

Today's signals to look for: bare `await Should.NotThrowAsync(...)` with no follow-up read of state; `.ShouldNotBeNull()` as the only assertion on a method that throws on null inputs; `[InlineData]` rows that walk through values for a branchless method.

### 15. Test discipline (NOTE) — see also categories 8, 9

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

### 16. Build and test gate (BLOCK)

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
