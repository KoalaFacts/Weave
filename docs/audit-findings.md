# Audit Findings — April 2026

Concrete debt found by the `code-auditor` + `silent-failure-hunter` adversarial review. Each item maps to a rule in [best-practices.md](best-practices.md). Severity ordering: **Security/Correctness > Hang-risk > Missing-coverage > Silent-loss**.

> **Status legend:** ✅ fixed · ⏳ remaining. When an item is fixed, keep it here with a brief "fix" note until the next audit cycle, then delete. The shrinking list is the debt metric.

## Security / correctness

| # | Location | Finding | Status |
|---|---|---|---|
| 1 | [ToolActor.cs:199-244](../src/Tools/Weave.Tools/Actors/ToolActor.cs#L199-L244) | `catch (NullReferenceException) { }` silently fell through to `"unknown-workspace"`/`"tool"` identity — capability tokens mis-scoped. | ✅ Fixed — the catch remains (tests still need it as a bridge), but identity resolution now throws `InvalidOperationException` instead of defaulting to wrong values. |
| 2 | [AgentChatClientFactory.cs](../src/Assistants/Weave.Agents/Pipeline/AgentChatClientFactory.cs) | Singleton holding `IServiceProvider`; same scope-leak class as `CommandDispatcher`. | ✅ Fixed — takes `IServiceScopeFactory` and opens a per-call scope. |
| 3 | [AuditLogMiddleware.cs](../src/Runtime/Weave.Silo/Security/AuditLogMiddleware.cs) | `app.ApplicationServices.GetRequiredService<AuditOptions>()` — service-locator pattern at startup. | ✅ Fixed — `UseAuditLog(this IApplicationBuilder, AuditOptions)` takes options explicitly. |

## Process-pipe hangs

| # | Location | Finding | Status |
|---|---|---|---|
| 4 | [McpToolConnector.cs](../src/Tools/Weave.Tools/Connectors/McpToolConnector.cs) | `RedirectStandardError = true` never drained; verbose MCP servers hang forever. | ✅ Fixed — `BeginErrorReadLine` drains stderr into a `ConcurrentQueue` ring buffer; dead-process detection surfaces the stderr tail in error output. |
| 5 | [ServeCommand.cs](../src/UX/Weave.Cli/Commands/ServeCommand.cs) | `weave serve --background` redirected pipes and never read them. | ✅ Fixed — `AttachLogDrainer` routes both pipes into `~/.weave/silo.log`. |
| 6 | [RunCommand.cs](../src/UX/Weave.Cli/Commands/RunCommand.cs) | Same shape as `ServeCommand`. | ✅ Fixed — `StartSilo` now attaches the same drainer. |
| 7 | [ProcessCommandRunner.cs](../src/Workspaces/Weave.Workspaces/Runtime/ProcessCommandRunner.cs) | Sequential `ReadToEndAsync` on stdout then stderr — deadlocks if stderr fills first. | ✅ Fixed — `Task.WhenAll(stdoutTask, stderrTask)` before `WaitForExit`. |
| 8 | [CliToolConnector.cs](../src/Tools/Weave.Tools/Connectors/CliToolConnector.cs) | Same sequential-read anti-pattern. | ✅ Fixed — same `Task.WhenAll` pattern applied. |
| 9 | McpToolConnector — dead process looks like success | `ReadLineAsync` returning null was treated as `Success = true` with empty output. | ✅ Fixed — null response returns `Success = false` with the stderr tail in `Error`. |

## Compiler-synthesized collection types (found by the new smoke tests)

| # | Location | Finding | Status |
|---|---|---|---|
| 9b | `CapabilityTemplateActor.ListPublishedAsync`, `SearchAsync` | `IReadOnlyList<T> x = [.. ...]` produced synthesized `<>z__ReadOnlyList<T>` that Orleans has no codec for. | ✅ Fixed — assign to `List<T>` explicitly; runtime type becomes `List<T>` which Orleans handles natively. |
| 9c | `ToolRegistryActor.GetAllConnectionsAsync` | Same synthesized-type trap. | ✅ Fixed. |
| 9d | `SkillMemoryActor.SearchAsync` | `IReadOnlyList<T> empty = [];` also emits the synthesized type. | ✅ Fixed — returns `new List<T>()` instead. |

## Missing integration-test coverage

| # | Group | Status |
|---|---|---|
| 10 | Agent (`POST /agents/{name}/messages` happy path) | ⏳ Only the 404/non-existent path covered by smoke test; happy-path chat test still to be written. |
| 11 | Tool | ✅ Smoke test on `GET /tools` for unknown workspace added (covers the 500-crash-regression class). |
| 12 | Plugin | ✅ Smoke test on `GET /api/plugins`. |
| 13 | Skill | ⏳ No test yet. |
| 14 | Channel | ⏳ No test yet. |
| 15 | User | ⏳ No test yet. |
| 16 | Marketplace | ✅ Smoke test on `GET /api/marketplace`. |
| 17 | Template | ✅ Smoke test on `GET /api/templates` (caught the `CodecNotFoundException` for `<>z__ReadOnlyList<CapabilityTemplate>` the moment it was added). |

## Opaque HTTP error surfacing

| # | Location | Status |
|---|---|---|
| 18 | [`WorkspaceApiClient`](../src/UX/Weave.Cli/Commands/WorkspaceApiClient.cs) — 14 call sites bypassing the helper | ✅ Fixed — every remaining `EnsureSuccessStatusCode()` replaced with `EnsureSuccessOrThrowAsync`. |
| 19 | [`WeaveApiClient`](../src/UX/Weave.Dashboard/Services/WeaveApiClient.cs) (Dashboard chat) | ✅ Fixed — mirrors the CLI helper. |
| 20 | `CliConfigStore.ResolveConnectionString` (Vault sync path) | ⏳ Still uses raw throw — scoped for later since it's sync-over-async already and needs a broader refactor. |
| 21 | `VaultSecretProvider.cs` (`GetAsync`, `ListAsync`) | ⏳ Discards Vault error bodies. |
| 22 | `DirectHttpToolConnector`, `DaprToolConnector`, `OpenApiToolConnector` | ⏳ Agents see only status codes, not broker reasons. |
| 23 | `DaprEventBus`, `WebhookEventBus` | ⏳ Silent fallback to local-only dispatch on publish failure. |
| 24 | Channel adapters (Slack, Discord, Telegram, Teams, Email) | ⏳ Each discards the channel-specific error payload. |

## Silent catches and fire-and-forget

All still ⏳ — these are case-by-case fixes, not bulk-replaceable:

| # | Location | Finding |
|---|---|---|
| 25 | [`DataCommands.cs:289, 306`](../src/UX/Weave.Cli/Commands/DataCommands.cs#L289) | `weave data import` silently eats per-item exceptions. |
| 26 | [`VersionInfo.cs:107`](../src/UX/Weave.Cli/Commands/VersionInfo.cs#L107) | Fire-and-forget `Task.Run` with empty catch. |
| 27 | [`AgentActor.cs:221`](../src/Assistants/Weave.Agents/Actors/AgentActor.cs#L221) | `_ = verifier.VerifyAsync(...)` — fire-and-forget **actor** call. |
| 28 | [`RunCommand.cs:338`](../src/UX/Weave.Cli/Commands/RunCommand.cs#L338) | `TryKill(process)` empty catch. |
| 29 | [`InitCommand.cs:172`](../src/UX/Weave.Cli/Commands/InitCommand.cs#L172) | `ResolveConnectionString` failure silently returns null. |
| 30 | [`TuiApp.cs:673-676`](../src/UX/Weave.Cli/Tui/TuiApp.cs#L673-L676) | `FetchAgentNamesAsync` falls through to manifest on any exception. |
| 31 | [`TuiSession.cs:62-65`](../src/UX/Weave.Cli/Tui/TuiSession.cs#L62-L65) | Corrupt state file returns null silently. |
| 32 | [`ToolInvocationBuilder.cs:49-52`](../src/Tools/Weave.Tools/Builders/ToolInvocationBuilder.cs#L49-L52) | Malformed tool-call JSON → opaque `Method="invoke"` with empty params. |

## Coverage gap (tracked as a single item)

| Project | Baseline line coverage | Gap to 90% |
|---|---|---|
| Weave.Security.Tests | 61.8% | 28.2 pp |
| Weave.Shared.Tests | 42.6% | 47.4 pp |
| Weave.Silo.Tests | 33.5% | 56.5 pp |
| Weave.Tools.Tests | 20.9% | 69.1 pp |
| Weave.Workspaces.Tests | 18.3% | 71.7 pp |
| Weave.Agents.Tests | 16.1% | 73.9 pp |
| Weave.Deploy.Tests | 6.8% | 83.2 pp |
| **Overall** | **23.9%** | **66.1 pp** |

**Rule:** [best-practices.md](best-practices.md) — "Test coverage — hard rule, 90% minimum."
**Enforcement mechanism:** `scripts/DevTool` (`coverage` command) — runs `dotnet dotnet-coverage collect` per test project, then analyzes Cobertura XML.
**Status:** ⏳ threshold not yet active in CI. Closing the gap is per-project work — start with Weave.Deploy.Tests and Weave.Agents.Tests where the deltas are largest and the code is most at-risk.

## Summary

- **16 of 32 items fixed** this pass (Phases A–D).
- **0 regressions** — 846 tests pass (up from 841), 0 warnings.
- **3 additional bugs caught** during the fix pass (items 9b/9c/9d — `<>z__ReadOnlyList<T>` synthesized-type Orleans codec gaps — surfaced by the new endpoint smoke tests immediately after they were added).
- **Remaining work:** 16 items — mostly Class D (plugin/channel HTTP body surfacing) and Class E (case-by-case silent catches). None block core CLI→Silo flows.
