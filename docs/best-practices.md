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

### Namespace hygiene and project layout

**One reason to exist per project.** If a project wraps adapters for a single consumer, it belongs *in* that consumer or as a folder under it. `Weave.Shared.Orleans` as a standalone Foundation project was wrong; its one consumer was the Silo, and it now lives at `src/Runtime/Weave.Silo/Serialization/`.

**Never name a project with a derivative suffix (`.Orleans`, `.Abstractions`, `.Core`) unless it carries weight across three or more consumers.** Derivative names signal an architectural gap, not a real module.

**Dependencies flow `Shared -> Workspaces -> Agents/Tools/Security/Deploy -> Silo/Cli/Dashboard -> AppHost`.** New circular references are rejected. If a Foundation type needs an adapter, the adapter moves to the consumer — Foundation does not take a dependency on it.

**Each storage/transport provider lives in its own opt-in project.** When an abstraction has multiple implementations that pull different third-party packages, each impl gets its own project (`Weave.Security.{Sqlite,Postgres}`, `Weave.Silo.Clustering.{Redis,Sqlite,SqlServer,Postgres}`). The abstractions project pulls zero provider packages — anyone wanting only one backend should be able to drop the other project refs and ship without those deps. After adding/removing a provider package anywhere in the graph, regenerate every consumer's `packages.lock.json` from a clean restore — central transitive pinning leaves stale entries that hide the win.

**Feature-based folders. No `Controllers/`, `Services/`, `Models/` at the top of a project.** Group by capability: `Workspaces/`, `Chat/`, `Heartbeat/`. See `src/Assistants/Weave.Agents/Actors/` — interface, implementation, and state model sit together.

### Versioning and breaking changes

**Pre-1.0: no backward-compat shims.** No `Legacy*` constants, no dual config keys for the same setting, no deprecated synonyms (`"postgres"` aliasing `"postgresql"`), no fallback property reads, no compatibility ctor overloads. When a key/type/contract changes, change the call sites and move on. Half the codebase is still under construction; carrying shims for an unreleased product is dead weight that hides which surface is the real one. Re-introduce migration shims only after a 1.0 release.

**When you remove a config value, grep the literal across the whole repo before claiming done.** Test fixtures (`[InlineData(...)]`), docs, sample configs, and CLI emit-side switches all hold copies of the string that the type-checker won't catch. `grep -rn '"the-removed-value"' .` is the floor.

### Refactoring discipline

**A refactor is a strict no-op for runtime behavior.** Moving code, splitting projects, renaming types — none of those should change what the running system does. If you catch yourself adding `RegisterFactory(...)`, an extra `?? defaultValue`, or "improvements" while moving code, stop and revert. Those are separate commits at minimum. The Silo clustering split nearly shipped three unintended `DbProviderFactories.RegisterFactory` calls disguised as part of the refactor — caught only because the original code clearly didn't have them.

**After any package or project graph change, force-regen every consumer's `packages.lock.json` from a clean restore.** `dotnet restore --force` only re-restores the requested project; downstream consumers stay on the old graph. The floor is `find src -name packages.lock.json -delete; find src -name obj -type d -prune -exec rm -rf {} +; dotnet restore Weave.slnx --force`. Stale lockfiles after the security split made an audit report "all clean" while 225 lines of `Sqlite/Npgsql/SQLitePCLRaw` pins still sat in 4 downstream lockfiles.

**When a rule applies, apply it everywhere it fits.** "But this is the composition root," "this only ships once," "this is just hygiene" — those are the rule talking back, not exceptions. The per-provider-project rule was applied to `Weave.Security` then carved out for `Weave.Silo` on the grounds that "no upstream domain project gets polluted" — until the user pushed back and the same mechanical refactor landed cleanly. Carve-outs accumulate into "rules nobody actually follows."

**Audit on fresh state.** Before reporting "I checked X and it's clean," regenerate any cached/derived artifact you read from — lockfiles, generated source, build outputs, test reports. Stale derived state will tell you "all good" when the underlying change hasn't propagated. The same audit twice (once on stale lockfiles, once after `dotnet restore --force`) gave opposite answers in this session.

### Naming and style

**Async production methods end in `Async`.** Enforced by the editorconfig. Exceptions: `Main`, expression-bodied event handlers, `IDisposable` patterns. Tests do not require the suffix, but consistency is preferred.

**Interface names drop the `I` prefix in TypeScript code only; .NET keeps the `I`** (`IToolConnector`, `IPublisher`). The no-`I` rule in global CLAUDE.md is a TS convention — do not apply it to .NET.

**Extension classes in .NET follow `ExtensionsToXXX`**, sit in the target type's namespace, and suppress the namespace-mismatch analyzer inline. Do not invent new naming — the convention is already set in `CLAUDE.md`.

**Classes over 200 lines require a design check.** Before growing a class beyond 200 lines, consider whether responsibilities should be split into smaller focused types. Do not mix multiple production classes in one file unless they are tightly coupled private helpers; one public or internal production class per file is the default.

**Console output uses text-presentation Unicode, not emoji-variant glyphs.** `✗` (U+2717) renders with color tags; `✖` (U+2716) triggers emoji fonts that ignore Spectre RGB colors. If you must use a dual-use glyph, append U+FE0E to force text presentation. Use helpers in `CliTheme` rather than raw `Console.WriteLine` or direct Spectre markup.

### File and class size

**Production files: one public/internal type per file, classes ≤ 200 lines.** The 200-line threshold is a design check trigger, not a hard cap — but every file above it should have a paragraph in its PR description explaining why it didn't split. Today's only production violator is `src/UX/Weave.Cli/Tui/TuiSlashCommandDispatcher.cs` (422 lines) — slated for extraction into per-command handlers.

**Tightly-coupled type pairs may share a file when neither is meaningful alone.** The codified pattern is the CQRS shape: a `*Query` record plus its `*Handler` class in the same file (`GetRecentCapabilityAuditQuery.cs`). The handler is private to the query in practice, even though both are `public`. Two unrelated types that just happen to live in the same namespace do not qualify.

**Test files: keep under ~500 lines per type under test.** When a test file passes 600 lines it almost always means the production class is doing too much — split the production type first, the tests follow. Today's outliers (`FileSystemToolConnectorTests.cs` at 1575, `PublisherTests.cs` at 864, `AgentActorTests.cs` at 789) are honest signals about their respective production classes.

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

### Test quality — rules that prevent gaming coverage

Coverage alone is a **trailing** indicator of test quality. A suite can hit 95% line coverage while catching none of the bugs that actually ship. The rules below are what stop the number from lying.

**Every `[Fact]` / `[Theory]` ends with at least one meaningful assertion.** A test body that only calls the system under test and returns is a smoke test masquerading as a behaviour test — it contributes to line coverage but not to regression catching. Shouldly assertions count; `Assert.True(true)` / `ShouldNotBeNull` on a value you never inspect again do not. Review rejection: any test with zero `Should*` calls.

**Tests assert behaviour, not implementation.** `service.Method()` returning the right *value for the caller* is the bar. Asserting on private internal state or mock-call counts (`substitute.Received(3).Method(...)`) is a red flag — it locks in how the code works today and breaks any refactor. Prefer outcome-based assertions; if that's impossible, the code probably has too many collaborators.

**One behaviour per test.** Name as `MethodName_Condition_ExpectedResult` (already in `CLAUDE.md`). Multiple `ShouldBe` calls that describe a single outcome are fine; multiple *scenarios* in one test body means split it — a failure must name the exact behaviour that broke.

**Arrange-Act-Assert, visible.** Each test is three sections: setup, the one call you're testing, and assertions. No interleaving. If you can't tell where Act ends and Assert begins, the test is testing too much.

**No `try/catch` in tests.** Use `Should.Throw<T>()` for expected exceptions. A `try/catch` that swallows an exception + continues IS a test that silently passes under failure conditions.

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
