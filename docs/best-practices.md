# Weave Best Practices

The law of the repo. Every rule here is enforceable in review. Rules exist to prevent concrete failures we have already paid for — do not relax one without a PR that removes it.

## Code conventions

### DI and lifetimes

**Pick the right lifetime before reaching for `IServiceScopeFactory`.** If you feel pulled toward a scope factory, the service is almost certainly registered with the wrong lifetime. Service-locator patterns (`scopeFactory.CreateScope()` + `GetRequiredService`) hide real dependencies from the compiler and turn runtime errors into design drift. In this repo the default for stateless routers (dispatchers, factories) is **`Scoped`**, so the injected `IServiceProvider` is the consumer's own scope — see `src/Foundation/Weave.Shared/Cqrs/CommandDispatcher.cs` and `QueryDispatcher.cs` (both scoped, no scope factory).

**`IServiceScopeFactory` is reserved for services that legitimately outlive a request.** Background services (`IHostedService`), detached work (fire-and-forget that needs DI), and any singleton that must perform per-iteration scoped work. For those, the factory is correct; everywhere else it's a smell that should be challenged in review.

**CQRS handlers are `Scoped`; dispatchers are `Scoped`.** The source-generated `AddGeneratedCqrsHandlers()` registers dispatchers and handlers as Scoped so the lifetime matches the caller's (HTTP request scope in endpoints, actor scope in Orleans). Don't override to `Singleton` — that's the bug we just retired.

**Actors receive dependencies through constructor injection only.** Do not pull services inline from `provider runtime context` or a static accessor — it breaks the "tests may instantiate actors directly" rule in `CLAUDE.md`. Follow `src/Assistants/Weave.Agents/Actors/` for the shape.

**Every service crossing a module boundary is programmed against an interface.** Concrete classes wrap third-party libraries; consumers never import them directly. Applies to `IToolConnector`, `ISecretProvider`, `IPublisher`. Tests substitute with `NSubstitute` without touching the real stack.

**Middleware resolves per-request services from `HttpContext.RequestServices`, never from `app.ApplicationServices`.** `app.ApplicationServices` is the root provider — exactly the same trap that killed `CommandDispatcher`. Any middleware that pulls state from DI has to go through `HttpContext.RequestServices` or cache a singleton upfront.

**Stateless factories are Scoped, not Singleton.** `AgentChatClientFactory` is Scoped so its injected `IServiceProvider` is the consumer's own scope (HTTP request or Orleans actor), and `ActivatorUtilities.CreateInstance` resolves any scoped middleware dependencies correctly. Making such a factory Singleton creates an asymmetry: the factory outlives the request, but the clients it constructs depend on scoped services — that's the same bug as the dispatcher's.

**Static classes are for constants, pure functions, extension methods, and codegen — not for things that should take dependencies.** Acceptable: `WeavePorts` (port constants), `CliTheme` (markup helpers), `TuiCommandParser` (pure parsing), C# extension methods (forced by the language), source-generator output. Wrong: a `static class FooCommand { public static Task ExecuteAsync(IServiceProvider sp, ...) }` whose every call does `sp.GetRequiredService<...>()` — that's service-locator with extra steps. Register `FooCommand` as a class, take dependencies via constructor, expose an instance method. Today's CLI uses `static class XCommand { Create() }` to *build* a `System.CommandLine.Command` tree, which is fine — but the action body the `Create()` returns must inject through the parser's `IServiceProvider`, not via static helpers underneath.

### Error handling

**Never call `EnsureSuccessStatusCode()` on an `HttpResponseMessage` whose body may contain `ProblemDetails`.** It throws with only the status code, discarding the server's reason. Use the `EnsureSuccessOrThrowAsync` + `FormatHttpError` helper in `src/UX/Weave.Cli/Commands/WorkspaceApiClient.cs` so the user sees the actual 409 conflict reason, not "409 Conflict" alone.

**HTTP clients read ProblemDetails on failure and surface the `title`, `detail`, and `errors` fields.** The Silo emits RFC 7807 problem responses from `src/Runtime/Weave.Silo/Api/ResultExtensions.cs`; the CLI must parse them. If you add a new API client, copy the helper shape — do not reimplement.

**Fail closed on security-sensitive operations.** Secret scanning, capability validation, and proof validation must default to deny when anything is ambiguous. See the secret-redaction behavior in `src/Foundation/Weave.Shared/Secrets/SecretValue.cs` — `ToString()` returns `"***REDACTED***"`, not the value.

**Swallow nothing silently.** Catch-and-log is acceptable only at a true boundary (HTTP handler, actor reminder, CLI command root). Inside a method, let it throw.

**No bare `catch { }` or `catch (Exception) { }` in `src/`.** Every catch must satisfy both: (a) a specific exception type, and (b) a log at `Warning` or higher with context, or a rethrow. `OperationCanceledException` on cooperative shutdown is the only exempt case and must still be typed. Review blocker: any unexplained empty catch.

**Never `catch (NullReferenceException)`.** NREs from library code are bugs — silencing one always makes the real failure surface somewhere worse (wrong workspace, wrong user, wrong capability). Fix the null, don't catch it.

**Probe helpers catch only network-layer exceptions.** `IsReachableAsync`, readiness loops, health pollers — the only catch list is `HttpRequestException`, `TaskCanceledException` (timeout), `SocketException`. Bare `Exception` masks `UriFormatException` from a bad config and makes "silo not running" indistinguishable from "your cert chain is broken". Log the captured exception at `Debug` so operators running with verbose logs can diagnose.

**Background tasks observe their own faults.** `_ = Task.Run(async () => { ... })` with no error handler is forbidden — a detached exception is unlogged and never surfaces. Use `await`, `task.ContinueWith(t => log, TaskContinuationOptions.OnlyOnFaulted)`, or a named helper `FireAndForgetAsync(task, logger, operationName)`.

**Fire-and-forget actor calls are forbidden outright.** `_ = actor.SomeAsync()` discards an Orleans activation failure or serialization mismatch and leaves the caller wedged in a half-dispatched state. Always `await`, or capture the task and observe completion elsewhere. Reentrancy concerns (an actor calling back into itself) are not a license to fire-and-forget — use `[AlwaysInterleave]` on the inner method, restructure so the callback target is a different grain, or hand the work to a hosted background service. The current offender is `src/Assistants/Weave.Agents/Actors/AgentActor.cs` (search `_ = Task.Run`); fix it, do not codify it.

**Catch the simplest form that expresses intent.** `catch (IOException)` beats `catch (Exception ex) when (ex is IOException)` for a single type — same behavior, less ceremony. The `Exception ex when (...)` form is reserved for genuine multi-type filters where listing each `catch` block would duplicate the body. Today's worst offender: `src/UX/Weave.Cli/Tui/ChatComposer.cs` lines 122/125/131 use the verbose form for single-type swallows; same file lines 147/153 already use the simpler form for the same intent.

**Drop the bind variable when you don't use it.** `catch (IOException) { /* platform quirk */ }` is the form when there's nothing to log — naming `ex` and then ignoring it (`catch (IOException ex) { /* ignore */ }`) is dead code that compilers used to warn about. If you have a reason to keep the binding (future logging, debugger inspection), add a one-line `LogDebug` and lose the `/* ignore */`. Don't keep the binding "just in case."

### Process management

**Never set `RedirectStandardOutput = true` (or `RedirectStandardError = true`) on a `Process` without draining the pipe.** A child that writes more than ~4 KB without a reader blocks forever. Use `BeginOutputReadLine` / `BeginErrorReadLine` plus `OutputDataReceived` / `ErrorDataReceived` handlers, and call `Start()` only after wiring them. The Silo auto-start in `src/UX/Weave.Cli/Commands/UpCommand.cs` follows this pattern — copy it.

**Redirect child-process output to a log file on disk, not into memory.** If the CLI wants to show tail on demand, tail the file. Buffering indefinite output into a `StringBuilder` is a memory leak on the happy path.

**If `RedirectStandardError = true`, drain stderr too.** Same pipe-buffer rule as stdout, but easier to miss because tests rarely cover verbose-stderr scenarios. Every connector that spawns a process must wire both pipes — `src/Tools/Weave.Tools/Connectors/McpToolConnector.cs` is the worked example, calling `process.BeginErrorReadLine()` after `RedirectStandardError = true`.

**When using `ReadToEndAsync` on both pipes, start both reads before awaiting either.** Sequential reads deadlock: if the child fills stderr while stdout is empty, you sit on `ReadToEndAsync(stdout)` forever. Use `Task.WhenAll(stdoutTask, stderrTask)` and then `WaitForExit`. The pattern is in `src/Workspaces/Weave.Workspaces/Runtime/ProcessCommandRunner.cs` — copy it.

**Background launchers (`weave serve --background`, `weave run --background`) must drain pipes or not redirect.** The Silo auto-start handler in `src/UX/Weave.Cli/Commands/Workspace/SiloProcessService.cs` is the worked example — it wires `OutputDataReceived` and `ErrorDataReceived` and calls `BeginOutputReadLine` / `BeginErrorReadLine` before the process produces output.

**Respect the `CancellationToken` at every async boundary.** `await foo(ct)`, `await Task.Delay(d, ct)`, `HttpClient.SendAsync(req, ct)`. CLI commands must cancel cleanly on Ctrl+C.

### Serialization adapters (Orleans)

**Every branded ID or shared value type that crosses a actor boundary has a surrogate + `[RegisterConverter]`.** Orleans will throw `CodecNotFoundException` at Silo startup (or worse, at the first actor call) if you forget one. Add the pair in `src/Runtime/Weave.Silo/Serialization/` next to the existing per-type surrogate files (`AgentIdSurrogates.cs`, `WorkspaceIdSurrogates.cs`, etc.); do not create a new assembly.

**Serialization adapters live in the consumer, not in Foundation.** `Weave.Silo.Serialization` owns them because the Silo is the only consumer. Do not recreate a `Weave.Shared.Orleans` project — adapters leak Orleans into Foundation and invert the dependency flow from `CLAUDE.md` (`Shared -> ... -> Silo`).

**State models that live in actor storage use `[GenerateSerializer]` + `[Id(n)]` on every field.** Appending a new field without an `[Id]` breaks wire compatibility for existing state. Never renumber existing `[Id]` values — only append.

**Actor interfaces accept and return branded IDs directly; actor keys remain `string`.** Convert to `string` only at the `IActorFactory.GetActor<T>(key)` call site, using the key shapes in `CLAUDE.md`. Mixing stringly-typed IDs in call arguments defeats the source generator.

### AOT, trimming, and platform guards

**Do not read `Console.CursorVisible`.** The getter is Windows-only and CA1416 fails the build on Linux/macOS. Only set it. See the console handling in `src/UX/Weave.Cli/Tui/TuiApp.cs`.

**Guard any P/Invoke or OS-specific API with `OperatingSystem.IsWindows()` / `IsLinux()` / `IsMacOS()`.** CA1416 is a warning-as-error in this repo. Pair platform-specific code with an `[SupportedOSPlatform]` attribute when appropriate.

**Prefer source-generated JSON, regex, and logging.** AOT-friendly and fail-fast at compile. Use `ManifestJsonContext` (workspace manifests), `SiloApiJsonContext` (HTTP API), `CliApiJsonContext` (CLI), and `[GeneratedRegex]` for all regex. A raw `new Regex("...")` in a hot path is a review blocker.

**Use `JsonSerializer.SerializeToUtf8Bytes` + `ByteArrayContent`, not `PostAsJsonAsync` with reflection overloads.** This keeps adapters AOT- and trimming-safe; reflection overloads break under both.

**Source generation over reflection — everywhere it's available.** STJ source-gen for serialization, `[GeneratedRegex]` for regex, `[LoggerMessage]` for structured logs, the repo's own `BrandedIdGenerator` for branded IDs and `CqrsRegistrationGenerator` for handler registration. The reflection equivalents (`JsonSerializer.Serialize<T>(value)`, `new Regex(pattern)`, `logger.LogInformation(...)`, `assembly.GetTypes().Where(...)`) all break under NativeAOT trimming and are slower under JIT. If a feature has a source generator, use it; if a hot path doesn't have one yet, either add one or annotate the call with `[RequiresUnreferencedCode]` so AOT consumers see the warning.

**Anonymous types in `JsonSerializer.Serialize` calls are reflection-only.** They cannot be added to a `JsonSerializerContext`, so every site using `JsonSerializer.SerializeToUtf8Bytes(new { text = ... })` falls back to runtime reflection. Today's offenders: `Weave.Silo/Channels/{Teams,Discord}ChannelAdapter.cs` (single-field payloads). Fix shape: a typed `record TeamsPayload(string Text)` plus a `[JsonSerializable(typeof(TeamsPayload))]` entry on `SiloApiJsonContext` (or a per-adapter context).

**Reflection-based DI registration is dead code.** `Weave.Shared/Cqrs/ServiceCollectionExtensions.cs::AddCqrs` carries `[RequiresUnreferencedCode]` and scans assemblies via `GetTypes()` — it has zero callers; production wires CQRS through the source-generated `AddGeneratedCqrsHandlers()`. Delete the reflection path per the pre-1.0 no-back-compat rule.

### Namespace hygiene and project layout

**One reason to exist per project.** If a project wraps adapters for a single consumer, it belongs *in* that consumer or as a folder under it. `Weave.Shared.Orleans` as a standalone Foundation project was wrong; its one consumer was the Silo, and it now lives at `src/Runtime/Weave.Silo/Serialization/`.

**Never name a project with a derivative suffix (`.Orleans`, `.Abstractions`, `.Core`) unless it carries weight across three or more consumers.** Derivative names signal an architectural gap, not a real module.

**Dependencies flow `Shared -> Workspaces -> Agents/Tools/Security/Deploy -> Silo/Cli/Dashboard -> AppHost`.** New circular references are rejected. If a Foundation type needs an adapter, the adapter moves to the consumer — Foundation does not take a dependency on it.

**Each storage/transport provider lives in its own opt-in project.** When an abstraction has multiple implementations that pull different third-party packages, each impl gets its own project (`Weave.Security.{Sqlite,Postgres}`, `Weave.Silo.Clustering.{Redis,Sqlite,SqlServer,Postgres}`). The abstractions project pulls zero provider packages — anyone wanting only one backend should be able to drop the other project refs and ship without those deps. After adding/removing a provider package anywhere in the graph, regenerate every consumer's `packages.lock.json` from a clean restore — central transitive pinning leaves stale entries that hide the win.

**Feature-based folders. No `Controllers/`, `Services/`, `Models/` at the top of a project.** Group by capability: `Workspaces/`, `Chat/`, `Heartbeat/`. See `src/Security/Weave.Security/{Tokens,Audit,Vault,Scanning}/` for the right shape — each folder owns its interface, implementation, state, options, and any helpers as a single vertical slice.

### Vertical slices and code organization

**Default to vertical slices: group by capability, not by technical role.** A feature folder owns its actor, state, commands, queries, events, and supporting types. Horizontal cuts (`Actors/`, `Queries/`, `Commands/`, `Events/`, `Models/`, `Services/`) collect every feature's slice of one technical role into a basket that grows monotonically and forces every feature owner to touch the same N folders. The exemplar is `Weave.Security/{Tokens,Audit,Vault,Scanning}/` — adding a new security capability adds one folder, not five entries spread across five baskets.

**Today's biggest horizontal-cut violators, in priority order for cleanup:**
- `Weave.Agents/{Actors,Commands,Queries,Events}/` — 36 files in `Actors/` alone, mixing 7 unrelated capabilities (agent, channel gateway, episodic memory, proof verifier/validator, skill memory, tool registry, user model). Should split into `Weave.Agents/{Agents,Channels,Memory,Proof,Skills,Tools,Users}/`, each owning its own actor + state + commands + queries + events.
- `Weave.Tools/{Actors,Events}/` — same shape, smaller scale. `Connectors/`, `Discovery/`, and `Marketplace/` already model the right pattern within this project.
- `Weave.Workspaces/{Actors,Commands,Queries,Events}/` — same shape; the slices are `Workspaces` and `Templates`, both already have folders that should absorb the rest.
- `Weave.Dashboard/Services/` — mixes `WeaveApiClient` with 8 DTOs. The client moves to `Api/`; each DTO co-locates with the Razor page that consumes it.

**Pluralized type-name folders are smells: `Models/`, `Services/`, `Helpers/`, `Utils/`, `Common/`, `Shared/` (inside a project), `Misc/`, `Managers/`, `DTOs/`.** Each one says "I didn't decide what this code is about." `Weave.Dashboard/Services/` is the only top-level offender today — others would be rejected on review.

**Composition-root infrastructure is the legitimate horizontal exception.** `Startup/`, `Api/`, `Configuration/`, `VirtualActors/`, `Serialization/` in `Weave.Silo` exist because the Silo wires every feature — they are not capabilities, they are wiring layers. The test for "is this exception OK": does this folder *have* to know about every feature in the project? If yes, horizontal is correct. If no, it's masking missing slices.

**Inside a feature folder, the triple — interface, implementation, and state model — sits together.** `IFooActor.cs` + `FooActor.cs` + `FooState.cs` next to each other. This is what's already done well *within* `Weave.Agents/Actors/`; the work is to lift the same per-feature grouping one level up so each capability is its own folder.

**A new feature adds one folder.** If adding "skill recommendations" requires touching `Actors/`, `Commands/`, `Queries/`, `Events/`, *and* `Models/`, the project is shaped wrong — a future change to that feature will sprawl across all five. The PR diff should be biased toward "many lines in one folder," not "one line in each of many folders."

### Versioning and breaking changes

**Pre-1.0: no backward-compat shims.** No `Legacy*` constants, no dual config keys for the same setting, no deprecated synonyms (`"postgres"` aliasing `"postgresql"`), no fallback property reads, no compatibility ctor overloads. When a key/type/contract changes, change the call sites and move on. Half the codebase is still under construction; carrying shims for an unreleased product is dead weight that hides which surface is the real one. Re-introduce migration shims only after a 1.0 release.

**When you remove a config value, grep the literal across the whole repo before claiming done.** Test fixtures (`[InlineData(...)]`), docs, sample configs, and CLI emit-side switches all hold copies of the string that the type-checker won't catch. `grep -rn '"the-removed-value"' .` is the floor.

### Split, merge, refactor

**Drive these decisions by observable principles, not by feel.** "It feels like two concerns" is not a justification — neither is "this class is too big." The rules below produce the same answer regardless of who applies them. If you cannot answer a principle's test with concrete evidence, you do not have grounds to act.

**Split a unit into multiple units when at least one is true:**

- **Different change rates.** Git history shows the parts evolve at different cadences, or a named upcoming change touches one but not the other. Not "feels different" — measurable.
- **Different consumers.** The parts are called from different sites, or one is a public contract while the other is an internal step.
- **Real replaceability.** The part is meant to be swapped (strategy, plugin, provider). Either ≥2 implementations exist today, or a named one is on the roadmap.
- **Different testability profile.** One is pure and unit-testable, the other needs heavy mocks or integration. Splitting unblocks tests that the merged form cannot have.

**Merge units into one when all are true:**

- **Co-change.** Every modification to one part touches the other (history, not speculation).
- **Single caller, single implementation.** One consumer, one impl, no roadmap for more.
- **No testability gain.** Splitting would not unlock any test the merged form blocks.

**Refactor the shape of code when either is true:**

- **Real duplication at N ≥ 3.** Identical (not coincidentally similar) logic in three or more places. Two is coincidence; three is a pattern.
- **Current shape blocks a named upcoming change.** The change is on the roadmap, not hypothetical.

**Anti-principles — these alone do not justify split, merge, or refactor:**

- Line count alone. ">200 lines" is a smell to investigate, not a trigger to act — investigate against the principles above.
- "Feels like two concerns" without git evidence or a named upcoming consumer.
- "Future flexibility" with no named caller.
- "While I'm here" cleanups inside an unrelated change — those are separate commits at minimum (see *Refactoring discipline* below).

**When the principles disagree or the evidence is weak, do not act.** The default is to leave the existing shape alone. A speculative split costs less to add later than to remove from a live codebase.

**`*Helpers`, `*Utilities`, `*Common`, `*Manager` classes are an anti-pattern.** They mark a class with no identity beyond "place where things go" — a junk drawer that accumulates orphan methods until nobody can refactor around it. Before reaching for one, ask in order: (a) can this inline at the call site? (b) is there a `private static` home in the one class that needs it? (c) is this an extension method on its operand's type? (d) is there a missing domain type whose behavior this actually is? "Make it a helper class" is the symptom of skipping that question. The same suspicion applies to a `static class FooScorer` / `FooCalculator` / `FooProcessor` whose entire surface is loose `static` methods called from one or two places — those are helpers in disguise. The narrow exception is verb-shaped pure-function modules with ≥3 distinct domain consumers; below that bar, inline or attach to a real owner.

**Return types and properties default to the immutable form.** `IReadOnlyList<T>` over `List<T>`, `IReadOnlyDictionary<K,V>` over `Dictionary<K,V>`, `IReadOnlyCollection<T>` / `IReadOnlySet<T>` over their mutable bases, `init`-only over `set`. The mutable concrete type is acceptable in two narrow places: (a) private fields and local variables where ownership is unambiguous, and (b) Orleans-state record properties that the actor mutates in place before `WriteStateAsync` (the in-place pattern is the storage contract, not an API leak). Anywhere a caller reads — method return types, public properties on non-state types, DTOs crossing a module boundary — defaults to the immutable view. A caller that needs to mutate constructs a new value; it does not modify what it received.

### Refactoring discipline

**A refactor is a strict no-op for runtime behavior.** Moving code, splitting projects, renaming types — none of those should change what the running system does. If you catch yourself adding `RegisterFactory(...)`, an extra `?? defaultValue`, or "improvements" while moving code, stop and revert. Those are separate commits at minimum. The Silo clustering split nearly shipped three unintended `DbProviderFactories.RegisterFactory` calls disguised as part of the refactor — caught only because the original code clearly didn't have them.

**After any package or project graph change, force-regen every consumer's `packages.lock.json` from a clean restore.** `dotnet restore --force` only re-restores the requested project; downstream consumers stay on the old graph. The floor is `find src -name packages.lock.json -delete; find src -name obj -type d -prune -exec rm -rf {} +; dotnet restore Weave.slnx --force`. Stale lockfiles after the security split made an audit report "all clean" while 225 lines of `Sqlite/Npgsql/SQLitePCLRaw` pins still sat in 4 downstream lockfiles.

**When a rule applies, apply it everywhere it fits.** "But this is the composition root," "this only ships once," "this is just hygiene" — those are the rule talking back, not exceptions. The per-provider-project rule was applied to `Weave.Security` then carved out for `Weave.Silo` on the grounds that "no upstream domain project gets polluted" — until the user pushed back and the same mechanical refactor landed cleanly. Carve-outs accumulate into "rules nobody actually follows."

**Audit on fresh state.** Before reporting "I checked X and it's clean," regenerate any cached/derived artifact you read from — lockfiles, generated source, build outputs, test reports. Stale derived state will tell you "all good" when the underlying change hasn't propagated. The same audit twice (once on stale lockfiles, once after `dotnet restore --force`) gave opposite answers in this session.

**Dead code is a sign you stopped paying attention.** When you remove a caller, the called code may have become orphaned. When you remove a config key, its constants and the helpers that read it are next. When you replace a reflection path with source-gen, the reflection helpers are dead. After any deletion or contract change, grep for the removed name and the immediate neighbours — symbols whose only caller was what you just removed are now garbage. Examples from this branch: deleting `Legacy*` constants left `LegacyOrleansStorageSectionName` references in `RuntimeSettings` until a follow-up scan; the source-gen rule landed only when `ServiceCollectionExtensions.AddCqrs` (`[RequiresUnreferencedCode]`, zero production callers, only its own tests referenced it) was finally noticed and deleted. Dead code accumulates until someone refactors blind, can't tell which path is real, and breaks the live one. Delete in the same commit that orphans it.

**False-positive watchlist for "unused" greps.** Some callers don't show up in `grep -r`: `System.CommandLine` command builders are referenced by `Program.cs` `Command` tree composition; CQRS handlers are wired by source-generated `AddGeneratedCqrsHandlers()`; Razor event handlers are called from `@onclick="@MethodName"` in the matching `.razor` file (not the `.razor.cs`); Orleans grain bridges are resolved by the cluster client from `IGrainWithStringKey` keys; `[JsonSerializable]`-attributed types are dispatched at runtime through the context. When marking something orphan, check those vectors before deletion.

### Naming and style

**Async production methods end in `Async`.** Enforced by the editorconfig. Exceptions: `Main`, expression-bodied event handlers, `IDisposable` patterns. Tests do not require the suffix, but consistency is preferred.

**Interface names drop the `I` prefix in TypeScript code only; .NET keeps the `I`** (`IToolConnector`, `IPublisher`). The no-`I` rule in global CLAUDE.md is a TS convention — do not apply it to .NET.

### Comments

**Default to no comments.** The variable name, method name, and type name should carry the meaning. A comment is justified only when the *why* is non-obvious — a hidden constraint, a subtle invariant, a workaround for a specific bug, behavior that would surprise a reader. If removing the comment wouldn't confuse a future reader, don't write it.

**Don't narrate what the next line does.** `// Get the user`, `// Loop through items`, `// Construct the request`, `// Returns the workspace` — these restate the code. Delete them; rename the symbol if it's still unclear.

**Don't reference the current task, fix, or callers** — "added for the X flow," "used by Y," "fixes #123." That context belongs in the PR description and rots as the codebase evolves. The exception: a comment that names a specific external bug (`// Workaround for dotnet/runtime#12345`) is durable because the link is permanent.

**XML doc summaries on public surfaces stay under 3 lines.** They explain *contract* — what params mean, what's returned, what's thrown, what side-effects exist — not implementation. A summary that runs into paragraphs usually means the type is doing too much; split before lengthening the doc. The current outliers worth reviewing: `Weave.Silo/Audit/CapabilityAuditSubscriberHostedService.cs` (14-line block — but the `<remarks>` documents a real startup-ordering hazard, so this one earns its length), `Weave.Shared/Cqrs/CommandDispatcher.cs` (13 lines), `Weave.Shared/Plugins/PluginServiceBroker.cs` (10 lines), `Weave.Agents/Pipeline/AgentChatClientFactory.cs` (10 lines).

**`<remarks>` is the right tool for non-obvious context** (startup ordering, threading model, lifetime quirks, why a seemingly redundant call is necessary). It's distinct from `<summary>` precisely so `<summary>` can stay short. Use it when you'd otherwise feel pulled to make `<summary>` longer.

**Extension classes in .NET follow `ExtensionsToXXX`**, sit in the target type's namespace, and suppress the namespace-mismatch analyzer inline. Do not invent new naming — the convention is already set in `CLAUDE.md`.

**Classes over 200 lines require a design check.** Before growing a class beyond 200 lines, consider whether responsibilities should be split into smaller focused types. Do not mix multiple production classes in one file unless they are tightly coupled private helpers; one public or internal production class per file is the default.

**Console output uses text-presentation Unicode, not emoji-variant glyphs.** `✗` (U+2717) renders with color tags; `✖` (U+2716) triggers emoji fonts that ignore Spectre RGB colors. If you must use a dual-use glyph, append U+FE0E to force text presentation. Use helpers in `CliTheme` rather than raw `Console.WriteLine` or direct Spectre markup.

### File and class size

**Production files: one public/internal type per file, classes ≤ 200 lines.** The 200-line threshold is a design check trigger, not a hard cap — but every file above it should have a paragraph in its PR description explaining why it didn't split. Today's only production violator is `src/UX/Weave.Cli/Tui/TuiSlashCommandDispatcher.cs` (422 lines) — slated for extraction into per-command handlers.

**Tightly-coupled type pairs may share a file when neither is meaningful alone.** The codified pattern is the CQRS shape: a `*Query` record plus its `*Handler` class in the same file (`GetRecentCapabilityAuditQuery.cs`). The handler is private to the query in practice, even though both are `public`. Two unrelated types that just happen to live in the same namespace do not qualify.

**Test files: keep under ~500 lines per type under test.** When a test file passes 600 lines it almost always means the production class is doing too much — split the production type first, the tests follow. Today's outliers (`FileSystemToolConnectorTests.cs` at 1575, `PublisherTests.cs` at 864, `AgentActorTests.cs` at 789) are honest signals about their respective production classes.

**Constructor dependencies: > 4 is a design-check trigger.** A `class` taking five or more injected services almost always violates SRP — it's coordinating too many seams and is a candidate for a facade extraction or a slice split. The rule applies to production `class` types only; `record` types (value objects, command/query DTOs) are exempt because their parameters are data fields, not seams. Tests, fixtures, and Orleans grain bridges that forward N domain deps to a single domain class are also exempt. Counts include primary-constructor and explicit-ctor parameters except `ILogger<T>`, `TimeProvider`, and `IOptions<T>` (cross-cutting infra). Like the 200-line rule, this is a trigger for a paragraph in the PR description, not a hard cap. Today's outliers (all in the Wave 5 split scope, so this rule and the vertical-slice rule point at the same actor sprawl): `ToolRegistryConnector` (7), `AgentActor` (7), `ToolActor` (6), `AgentLifecycle` (6), `ToolRegistryActor` (5).

### Tell-don't-ask on state records

**State records own their domain queries when the query has multi-field internal coupling.** When an actor's "compute X from state" or "mutate fields A+B+C atomically" reaches into multiple fields of its `IActorState<T>.State` and applies a non-trivial rule, that rule belongs on the state record as a method, not in the actor body. The actor stays a thin orchestrator (read state → authorize → call state method → write state → publish event); the corpus owns its query verb. The codified exemplars: `SkillMemoryState.Search` (multi-field scoring across tags + title + description, recency bonus, success-rate filter), `EpisodicMemoryState.Recall` (same shape with tag/agent/since filters), `AgentState.{SubmitTask,FailTask,AcceptTask,RejectTask,RefreshBusyStatus}` (task-lifecycle state machine), `UserProfileState.{RecordInteraction,BuildContextSummary,Clear}` (eviction + frequency counting + first/last-seen across 4 fields), `ToolRegistryState.{IsToolAllowed,GrantTools,ConfigureAccess}` (two-step access lookup, distinct-list normalization), `ChannelGatewayState.ResolveAgent` (target-agent → routing-rule pattern matching).

**Don't apply when the "method" is a one-line dictionary call.** `state.Channels.TryGetValue(id, out var c)` is a `Dictionary<TKey,TValue>` API, not a domain query — wrapping it as `state.GetChannel(id)` adds indirection without earning its keep. The codified passive bags: `AgentSupervisorState` (just `List<string>` of agent names; the actor's logic is "iterate and dispatch", not corpus-aware), `VerifierState` (configuration lists with default fallbacks; default-resolution is a one-liner). Future work that touches these consults this rule before adding methods.

**Quick self-check:** if you'd write the method body as a single `state.Field.Method(...)` call, leave it on the caller. If the body involves three or more state fields, a loop, or a non-trivial branching rule (eviction, scoring, state-machine transition), move it to the state record.

### Time and clocks

**Inject `TimeProvider`; never call `DateTime.UtcNow` / `DateTimeOffset.UtcNow` from logic that decides behavior.** "Logic that decides behavior" = anything with time-dependent control flow (cache TTL, token expiry, retry backoff, debounce windows, hint timeouts). Tests need `FakeTimeProvider` to drive these without `Thread.Sleep`. The pattern: `CapabilityTokenService` takes `TimeProvider` in its constructor and calls `_timeProvider.GetUtcNow()`; tests pass a `FakeTimeProvider` and call `Advance(TimeSpan)`.

**Default property initializers on data records (`= DateTimeOffset.UtcNow`) are the only acceptable direct call.** They exist purely so the field has a value when nobody set one. The writer (actor, command handler) should normally supply an explicit timestamp from its injected `TimeProvider`. Today's violators where logic depends on the wall clock and tests can't fake it: `Weave.Agents/Actors/AgentState.cs` (7 mutating writes), `Weave.Cli/Tui/ChatExitConfirmation.cs` (3 time-window checks), `Weave.Cli/Commands/Version/VersionService.cs` (cache TTL). Each should take a `TimeProvider`.

**Logging/display timestamps in CLI/TUI may use `DateTime.Now` directly.** They are not behavior — `SiloProcessService`'s log prefix and `TuiLiveStatusRenderer`'s "Refreshed" line do not feed any branching logic.

### Secrets and security

**Never log a `SecretValue.DecryptToString()` result.** Logs default to `.ToString()` which returns `"***REDACTED***"`. If you need the cleartext, you are inside a crypto boundary and should not be writing a log line there.

**Connection string templates have empty credential slots (`Username=;Password=`).** Defaults never carry invented creds — users fill in their own.

**Capability tokens gate tool access on the server side, not client side.** The CLI/TUI displays allowed tools, but the Silo enforces. Never add a client-side check as the only gate.

## Testing conventions

### Test pyramid and project layout

**Every production project has a sibling `.Tests` project.** No exceptions. If the project has no behavior worth testing, it probably shouldn't exist.

**Three tiers: unit, component, integration.** Unit tests exercise one type with all peers substituted. Component tests exercise a actor or a CQRS handler against a real `IServiceProvider`. Integration tests boot a process boundary. The Silo bug in session notes escaped because no integration test booted the Silo — we now require at least one smoke-boot test per top-level host.

**Silo-boot smoke test is non-optional.** `src/Runtime/Weave.Silo` must have a test that builds the host, starts it, pings one actor, and shuts it down. This would have caught the `AgentTaskId` `CodecNotFoundException` in CI, not in the TUI.

**Every endpoint group mapped in `Program.cs` has at least one `SiloFactory`-based integration test.** The Silo currently exposes 9 groups (Workspace, Agent, Tool, Plugin, Skill, Channel, User, Marketplace, Template); today only Workspace has any endpoint coverage. New endpoint → new test in the same PR. The test hits one real URL per group and asserts either the happy-path shape or the canonical error code; this is what would have caught the `409` DI-scope leak. See `src/Runtime/Weave.Silo.Tests/WorkspaceLifecycleTests.cs` for the template.

### Real components for integration tests

**Integration tests use real DI, real actor silo (in-memory `TestCluster`), real HTTP.** Mocking the host undermines the test. Use `Microsoft.Orleans.TestingHost` for Orleans, `WebApplicationFactory<T>` for the API.

**CQRS integration tests resolve the handler through `ICommandDispatcher`, not by instantiating it.** This exercises registration, scope creation, and the dispatcher wiring — all three were broken in the scoped-handler incident.

**HTTP clients under test use a `StubHandler : HttpMessageHandler`, not a mocked `HttpClient`.** `HttpClient` itself cannot be faked cleanly; its handler can. See the pattern in `src/Tools/Weave.Tools.Tests/` for connector tests.

### Fixtures and isolation

**Tests never share mutable actor state across classes.** Each test class owns its `TestCluster` or creates actors with unique keys. Cross-test contamination is the single most common flake source.

**No file-system side effects outside `Path.GetTempPath()`.** Writing into the repo or CWD breaks parallel test runs and pollutes the working tree. Clean up in `IAsyncDisposable.DisposeAsync`.

**Environment variables set in tests are scoped and restored.** Use `IDisposable`-backed helpers — never mutate `Environment.SetEnvironmentVariable` without a `finally` that resets it. Environment-detected plugins (Dapr when `DAPR_HTTP_PORT` is set) make this especially important.

**Tests against process-global instruments (`Meter`, `ActivitySource`, static counters) use thread-safe sinks and `ShouldContain`, never exact counts.** A static `Meter` is shared across the entire test assembly; any parallel test class that triggers the same instrument will land in your `MeterListener` callback. Use `ConcurrentQueue<T>` (the publisher fires on the call-site thread, racing your test thread — `List<T>.Add` corrupts under contention and produces cryptic Shouldly errors) and assert "snapshot contains the expected tag set" rather than "snapshot has exactly N items." See `CapabilityAuthorizerTests`'s metric tests for the pattern.

### Assertions and libraries

**Assertions use Shouldly. Never FluentAssertions.** Enforced by `CLAUDE.md`. If you need a custom assertion, write an extension on the value type in the test project.

**Nullable types are asserted non-null before property access.** `result.ShouldNotBeNull(); result.Name.ShouldBe("x");` — warnings-as-errors will reject `result!.Name` patterns. If the null-forgiving operator is the only option, leave a comment explaining why.

**One behavior per test.** Name as `MethodName_Condition_ExpectedResult`. Multiple asserts are fine when they describe one behavior; multiple *behaviors* means split the test.

**Expected failures are tested with `Should.Throw<T>()`.** Not `try`/`catch` + `Assert.Fail`.

### Mocking

**`ILogger<T>` for internal types uses `NullLogger<T>.Instance`** — NSubstitute cannot proxy non-public generics. `CLAUDE.md` calls this out.

**`AITool` cannot be substituted** — create a concrete stub that overrides `Name`. Same applies to any sealed or non-virtual surface.

**`private static` helpers that need direct testing are promoted to `internal static`.** Pattern: `ProofValidatorActor`. Do not make them `public` to test them.

### Categories and speed

**Unit tests complete in <50 ms each.** If a test takes longer, it is a component or integration test and belongs in the corresponding category.

**Integration tests are tagged with `[Trait("Category", "Integration")]`** so CI can run the fast suite on every push and the full suite on merge. Keep the fast suite under 60 seconds of wall-clock time.

**Flaky tests are quarantined, not retried.** Retry hides the bug. Mark with `[Fact(Skip = "tracking: <issue>")]` and fix within the sprint.

**CI exit-code mismatch is a Microsoft.Testing.Platform shutdown crash, not a failed test.** When the GitHub `Build & Test` job exits non-zero but the `Test Results` check shows every test passed (e.g. "All 2184 tests pass" + `Process completed with exit code 2` on the parent step), the orchestrator is propagating a crash that happened on a worker's *disposal* — the TRX has already been published, so the run is functionally green, but `dotnet test --solution X -- --report-xunit-trx …` inherits the worker's non-zero exit. Likely culprit: an in-process integration host (`SiloFactory : WebApplicationFactory<Program>`) whose Orleans cluster, ASP.NET host, or `IChatClient` background tasks fail to settle within the platform's shutdown window. **Diagnostic signal**: the `Test Results` published-test-result check (or the uploaded TRX artifact) shows zero failures; only the parent step's exit code is non-zero. **Action**: re-run the job before treating the diff as the cause. Don't hide it with `--ignore-exit-code` — if it fires repeatedly, audit the offending fixture's `IAsyncDisposable.DisposeAsync` to ensure every spun-up host (silo, web app, chat client, plugin host) is awaited to completion before the test class tears down.

### Test quality — rules that prevent gaming coverage

Coverage alone is a **trailing** indicator of test quality. A suite can hit 95% line coverage while catching none of the bugs that actually ship. The rules below are what stop the number from lying.

**Every `[Fact]` / `[Theory]` ends with at least one meaningful assertion.** A test body that only calls the system under test and returns is a smoke test masquerading as a behaviour test — it contributes to line coverage but not to regression catching. Shouldly assertions count; `Assert.True(true)` / `ShouldNotBeNull` on a value you never inspect again do not. Review rejection: any test with zero `Should*` calls.

**Tests assert behaviour, not implementation.** `service.Method()` returning the right *value for the caller* is the bar. Asserting on private internal state or mock-call counts (`substitute.Received(3).Method(...)`) is a red flag — it locks in how the code works today and breaks any refactor. Prefer outcome-based assertions; if that's impossible, the code probably has too many collaborators.

**One behaviour per test.** Name as `MethodName_Condition_ExpectedResult` (already in `CLAUDE.md`). Multiple `ShouldBe` calls that describe a single outcome are fine; multiple *scenarios* in one test body means split it — a failure must name the exact behaviour that broke.

**Arrange-Act-Assert, visible.** Each test is three sections: setup, the one call you're testing, and assertions. No interleaving. If you can't tell where Act ends and Assert begins, the test is testing too much.

**No `try/catch` in tests.** Use `Should.Throw<T>()` for expected exceptions. A `try/catch` that swallows an exception + continues IS a test that silently passes under failure conditions.

**Tests use real-shaped inputs, not the simplest values that compile.** `new AgentDefinition { Name = "a", Capabilities = [] }` proves nothing — every nullable is empty, the happy path runs straight through, no edge inside the SUT is exercised. Use a minimum representative payload: a real agent name, real capability strings (`"tool:git"`, `"skill:read"`), values long enough to hit any length-based branches. Helper factories (`AgentDefinitionFactory.Default()` then customize per test) keep this readable. The bar: if I changed the SUT to `return default`, would your assertions fire?

**A "verify-nothing" test is one whose assertions would still pass on a broken implementation.** Common shapes:
- `result.ShouldNotBeNull()` is the only assertion, but the SUT can never return null (it would throw first). The check encodes nothing.
- `result.Items.Count.ShouldBeGreaterThan(0)` when *any* implementation that returned a non-empty list would pass — including one that returned the wrong items.
- `(await Should.NotThrowAsync(() => sut.DoX()))` with no follow-up read of state. "Didn't throw" is not a postcondition for any feature this repo ships.
- Round-trip tests on records (`new Foo { X = 1 }.X.ShouldBe(1)`) — the C# compiler already guarantees this; you're testing the language.
- Mock-heavy tests where `substitute.GetValue().Returns(42)` then `result.ShouldBe(42)` — the test passed a value through a stub and read it back; nothing in the SUT was exercised.

For each test, ask: "what bug in the SUT would this catch?" If you can't name one, the test is verifying nothing.

**Mock the boundary, exercise the body.** A unit test mocks the SUT's *dependencies* and runs the SUT for real. If you find yourself `Substitute.For<TheSUT>()` and stubbing the very method you claim to test, you've inverted the harness. The result will pass on any implementation, including one that does nothing.

**Theory cases must be different.** `[Theory] [InlineData(1)] [InlineData(2)] [InlineData(3)]` over a method that doesn't branch on the value is one test, not three. Use `[InlineData]` to cover *distinct branches* (boundary values, empty/single/many, valid/invalid, fast-path/slow-path). The redundant-row check: removing one of the rows — does any branch in the SUT lose coverage? If no, the row was decoration.

**Mock only what you must.** A test with five `Substitute.For<T>()` calls is probably testing wiring that should be integration-tested instead. The smell threshold in this repo: more than 2 mocks per test = needs scrutiny.

**Integration tests use real components (already enforced).** Unit tests are allowed mocks; **integration tests are not**. See the rule in "Real components for integration" above. The boundary between the two is: "does this test run against a real `SiloFactory` / `TestCluster` / `WebApplicationFactory`?" If yes, no mocks.

**Tests never share mutable state.** Each test owns its fixture or creates actors with unique keys. Flakiness from test-to-test contamination is banned at the source, not retried around.

**Fixtures reset environment variables in `finally`.** Plugins in this repo are env-detected (`DAPR_HTTP_PORT`, `Vault:Address`). Leaking one across tests turns a unit test into an integration test.

**Test names describe the scenario, not the method.** `Silo_boots_and_health_check_returns_200` — not `TestHealthCheck`. A passing/failing test name should read like a fact about the system.

### Test coverage — hard rule, 90% minimum

**Every project's line coverage must be ≥ 90%. CI fails when any single project is below.** No exceptions; no per-project carve-outs. Exclusions from the coverage number are narrow and justified in `coverage.runsettings` — source-generated code, Program.cs, DTO/record-only files, and actor state models. Everything else counts.

**How to run the gate locally:**
```
dotnet tool restore
dotnet run --project scripts/DevTool -- coverage --threshold 90
```
The DevTool finds all test projects, runs `dotnet dotnet-coverage collect` per project, then parses the Cobertura XML. Exit code 1 = any project below threshold. Use `--skip-collect` to re-analyze existing results without re-running tests.

**Inner loop stays fast.** `dotnet test` without coverage runs as usual — `dotnet-coverage` is a local tool (`.config/dotnet-tools.json`) that wraps externally and only runs when invoked by the DevTool or CI. No coverage packages are added to test projects.

**Current baseline (captured at doc time):** overall **23.9%**. The 90% line is a target we're intentionally setting ABOVE current state — the enforcement gate is off in local dev until the gap closes. The gap is tracked as an item in `docs/audit-findings.md` and closes through real new tests, not coverage-gaming shortcuts.

**Coverage raised by assertion-free tests is rejected in review.** A PR that brings the number up by 2% while adding five tests that only call the SUT without asserting is worse than no change. The Test Quality rules above apply to every coverage-driven PR.

**Mutation testing is the gold standard for coverage validity.** Aspirational today: `Stryker.NET` run quarterly on critical paths (Silo API, CQRS dispatchers, actor state machines). A mutant survival rate below 80% means the tests exercise lines without asserting outcomes — exactly the gaming pattern the rules above try to prevent.

### Verification before claiming done

**Warnings are errors. `dotnet build Weave.slnx` must show zero warnings before pushing.** Release configuration, not just Debug.

**`dotnet format` runs clean before any PR.** Formatting diffs in a functional PR waste review time.

**A change that touches actor interfaces, actor state, or serialization requires an Orleans boot test.** Unit tests alone will not catch missing surrogates. The Silo-boot smoke test described above is the minimum bar.

---

Relevant file paths referenced above:

- `c:\projects\BeingCiteable\Weave\src\Foundation\Weave.Shared\Cqrs\CommandDispatcher.cs`
- `c:\projects\BeingCiteable\Weave\src\Foundation\Weave.Shared\Cqrs\QueryDispatcher.cs`
- `c:\projects\BeingCiteable\Weave\src\Foundation\Weave.Shared\Secrets\SecretValue.cs`
- `c:\projects\BeingCiteable\Weave\src\Foundation\Weave.Shared\Ids\BrandedIds.cs`
- `c:\projects\BeingCiteable\Weave\src\Runtime\Weave.Silo\Serialization\BrandedIdSurrogates.cs`
- `c:\projects\BeingCiteable\Weave\src\Runtime\Weave.Silo\Serialization\SharedTypeSurrogates.cs`
- `c:\projects\BeingCiteable\Weave\src\Runtime\Weave.Silo\Api\ResultExtensions.cs`
- `c:\projects\BeingCiteable\Weave\src\UX\Weave.Cli\Commands\WorkspaceApiClient.cs`
- `c:\projects\BeingCiteable\Weave\src\UX\Weave.Cli\Commands\UpCommand.cs`
- `c:\projects\BeingCiteable\Weave\src\UX\Weave.Cli\Tui\TuiApp.cs`
