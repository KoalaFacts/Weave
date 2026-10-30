---
name: adversarial-review
description: Adversarial self-review of staged or recent code, looking for the smells that grep-based auditors miss — dead branches, conflated UX messages, defensive nulls on contracts you control, tests that pass for the wrong reason, redundant DTO chains, style drift inside one file, over-engineering for private contracts. INVOKE THIS — proactively, without being asked — before every `git commit` that touches `src/`, after writing or rewriting any non-trivial code path, before saying "done" on a task that already passed `check-rules`. Pairs with `check-rules` (which catches mechanical/repo-policy violations); this one catches judgement-call smells. Reports a flat punch list with severity, file:line, and fix shape — does not auto-fix.
---

# adversarial-review — judgement-call smell-spot

## When to run

Run this skill, without being asked, before every `git commit` that touches `src/`. Also after rewriting a non-trivial code path, after migrating a feature, before saying "done" on any task that already passed `check-rules`. **`check-rules` catches what `grep` can find; this skill catches what `grep` can't.**

If you find yourself thinking "this is fine, I just shipped it" — that is the strongest signal you should run this. The author's confidence is the worst predictor of code quality.

This is **not** a full audit. Two minutes, six categories, punch list out. If a category turns up nothing, say so explicitly so the reader can tell silence-means-clean from silence-means-skipped.

## Source of truth

This skill encodes recurring smell patterns observed during reviews. The categories below are the ones that have actually shipped and had to be fixed in the same PR more than once. Add a category here when a smell shows up twice; remove one when it stops occurring.

## Audit procedure

Determine the scope first. For a fresh batch of work: the staged diff (`git diff --cached`) plus uncommitted (`git diff`) plus untracked source files. For a branch review: `git diff main...HEAD`. For a single commit: `git show HEAD`.

Then walk the categories below. Report findings as a flat punch list. Severity levels:

```
[BLOCK]  file:line — what's wrong → fix shape
[WARN]   file:line — what's wrong → fix shape
[NOTE]   file:line — what's wrong → fix shape
```

`BLOCK` = wrong UX or wrong logic. `WARN` = noisy / dishonest / over-engineered, fix in this PR. `NOTE` = mention but don't block.

End with an explicit clean section: `== adversarial-review: clean ==` if the diff has no findings, or `== checked but no findings: <categories> ==` for the categories you walked but found nothing.

### 1. Dead branches and unreachable arms (BLOCK)

Look for `catch (X)` clauses where the `try` body cannot throw `X`, `switch` arms that match impossible values, `if` guards on conditions that can't be reached.

Walk every new `catch` clause:
- Is the exception type actually thrown by anything inside the `try`?
- For wrapper exceptions (`HttpRequestException`, `JsonException`), does the API doc actually wrap that case?
- For domain exceptions, does the called method actually throw them?

Walk every new `switch` and `if`:
- For each case/branch, can a value reach it given the ingress contract?
- Defensive arms for "things that can't happen" are dead code that rots silently.

The smell example: `ManifestParser.Parse` throws only `JsonException`; a sibling `catch (FormatException)` was added "just in case". The sibling never fires; it lies in code review and the next reader has to re-derive the truth.

### 2. UX message conflation (BLOCK)

When a method returns a tagged failure type (`ActionFailure { Reason, Message }`, `Result<TError>`, etc.), the consumer must branch on `Reason` to render the right verb. Flag any caller that:
- Renders all failure reasons through the same template ("Configuration invalid: {message}").
- Uses the failure-message text as if it explains the failure category (e.g. silo unreachable surfaced as "Configuration invalid").
- Picks an exit code (`return 1`) without considering the reason.

Walk every new caller of an action / handler that returns a typed failure:
- Does it `switch (failure.Reason)` or does it have a single render path?
- For each render path, does the verb match the reason? "Invalid" for ValidationFailed, "Unreachable" for SiloUnreachable, "Not found" for NotFound, "Cancelled" for Cancelled.

The smell example: `if (!result.IsSuccess) WriteError($"Configuration invalid: {result.Failure.Message}")` renders SiloUnreachable as "Configuration invalid: Silo unreachable" — wrong UX, lies to the user about what failed.

### 3. Defensive null-coalesce on a contract you control (WARN)

When the action and the silo (or any two ends you wrote in the same PR) define the wire shape together, the contract is internal. Defensive null-handling on a non-nullable field is dead code that pretends the contract isn't yours.

Walk every new `?? <default>`:
- Is the left side actually nullable per the contract?
- If you wrote both ends, who would emit `null` for that field? If nobody, the `??` is dead.
- For collection wires: is the source-side property `IReadOnlyList<T>` (non-null per type) or `List<T>?` (could be null)? Match the wire to the source.

Walk every new wire DTO with `?` on collections:
- Could you make it non-null with default `[]` and drop the `??` at every call site?

The smell example: `Errors: wire.Errors ?? []` where the silo's `ValidateWorkspaceManifestResult.Errors` is typed `IReadOnlyList<string>` (always non-null). The `??` only fires if the silo emits literal JSON `null` — which contradicts the contract.

### 4. Tests that pass for the wrong reason (WARN)

A test verifies a code path only if its assertion can't be satisfied by any other path. Walk every new assertion:

- For string assertions (`Message.ShouldContain("X")`): is the substring `"X"` present in the test's stub data **and not** in any fallback / default the action might emit on the other code paths? If both, the assertion passes whether the right path ran or not.
- For exception assertions: did you actually trigger the exception, or did you assert via a stub that returned the exception type?
- For `.Received(N)` assertions: is N meaningful, or is it the count your impl happens to use?
- For round-trip assertions on records: the compiler already guarantees field round-trip; what does the test add?

Run the test mentally: rewrite the SUT to do nothing or return default. Do the assertions still pass? If yes, the test is decoration.

The smell example: stub returns `"Manifest is not valid JSON: Expected `:`"`; assertion is `Message.ShouldContain("not valid JSON")`. The action's fallback default is `"Manifest is not valid JSON."` — both contain "not valid JSON". The assertion passes whether or not the extraction logic works. Fix: assert against a marker substring only present in the stub (`<<MARKER>>`), or assert exact-equal to a value only the right path produces.

### 5. Redundant DTO chains (NOTE → WARN if duplication grows)

When data flows `domain → handler → endpoint → wire → action → curated`, each hop is justified only if it translates something. Walk every new chain:

- For each hop, what does it translate? Branded ID → string? Enum → string? Aggregate → projection?
- If a hop has the same field set as the one before it (modulo namespace), the hop is ceremonial duplication.
- Mappers like `FromX(...)` that just forward field-by-field are the loudest tell.

The pass-through pattern is fine when types are large and translation is real. It is wrong when the "DTO" is field-identical to the domain type.

The smell example: `ValidateWorkspaceManifestResult` (CQRS) → `ValidateWorkspaceManifestResponse.FromResult(result)` (HTTP) where `Response` has the same five primitive fields and `FromResult` is one-to-one. Drop the middle hop, return the CQRS result directly, register it in the silo's JsonContext.

### 6. Over-engineering for private contracts (WARN)

When you control both ends (silo + action, server + same-repo client), the contract is private. Public-API hardening (multi-key fallback chains, defensive validation paths, format-detection branches) is over-engineering — it pays for flexibility that nobody is buying.

Walk every new helper:
- Does the production code path actually exercise every fallback?
- Could you replace the helper with a one-line lookup, given what you know the other end emits?
- Is the function name vaguer than its real job? (`ExtractProblemMessage` is honest; `TryGetMessage` is over-broad if it only ever reads one key.)

The smell example: `ExtractProblemMessage` walks `errors.Values.First[0] ?? Detail ?? Title`, but the silo's only 400-emitting code path always writes a single `manifestJson` key. The fallbacks handle cases that don't exist. Replace with `errors.TryGetValue("manifestJson", out var msg) ? msg[0] : null` and rename to `ManifestJsonError`.

### 7. Style drift inside one file (NOTE)

Open every new file and ask: do all the records / types in this file use the same syntax style?

- Mix of primary-ctor records and property-init records in the same file.
- Mix of `init` and `set` on properties of related types.
- Mix of `[GeneratedRegex]` and `new Regex(...)`.

Pick one. Drift inside one file is the loudest "different person wrote each section" signal.

The smell example: `ValidateWorkspaceJsonContext.cs` had `ValidateWorkspaceWire(string ManifestJson)` (primary-ctor) next to `ValidateWorkspaceResultWire { ... }` (property-init) next to `ProblemWire { ... }` (property-init). Convert all to property-init since the rest of the codebase's wire types use it.

## Output shape

Always end with one of:

```
== adversarial-review: clean ==
```

or

```
== adversarial-review punch list (<N> findings) ==

BLOCK
  src/Foo/Bar.cs:42 — catch (FormatException) cannot fire; ManifestParser.Parse only throws JsonException → drop the arm

WARN
  src/Foo/BazTests.cs:88 — assertion matches both extracted message and fallback default → assert against marker substring only present in stub

NOTE
  src/Foo/Quux.cs — wire records mix primary-ctor and property-init styles → align to property-init
```

Always also report what you DIDN'T check (e.g. "skipped category 4 — no test changes in diff"), so the user can tell whether silence means "looked, found nothing" or "didn't look".

## Pairing with check-rules

`check-rules` runs first because it's cheap (`grep` / `find`). `adversarial-review` runs second because it requires reading the code with intent. Both should pass before commit. A `check-rules: clean` does NOT imply `adversarial-review: clean` — the most common mistake is to skip step two because step one passed.
