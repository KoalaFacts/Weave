# Finite command process lifecycle

## Supported path

CLI tool execution and workspace container-engine commands now use the same constructor-injected `IProcessRunner`. The Host registers one `ProcessRunner` singleton for both consumers. This is a finite invocation boundary; protocol workers and background Host launchers retain their separate lifecycles.

The runner starts an executable with an argument array and redirects both output streams. The existing CLI shell policy still runs before execution and retains its operation identity `exec`. Shell commands remain a shell privilege; this change does not turn shell parsing into a sandbox.

Each stream retains at most 65,536 UTF-16 characters, with two concurrent dedicated readers rather than shared worker-pool reads. Excess output signals termination immediately; readers continue draining until EOF and captured content is discarded for that result. Eight root executions are admitted per runner instance, including executions awaiting deferred cleanup. Capacity exhaustion returns a typed result immediately, without starting or queuing a child. The limit does not bound arbitrary descendants or other Host instances.

## Cancellation and cleanup

Cancellation before process creation prevents dispatch. Cancellation after creation and excess output request process-tree termination. Termination itself runs on a dedicated task, and termination, root exit and both readers share an independent five-second cleanup wait using injected `TimeProvider`. Confirmed cleanup preserves the caller's cancellation token and exception. Normal success still requires exit code zero and complete bounded capture of both streams.

If a detached descendant keeps an inherited pipe open after the root exits, cleanup can expire. The caller receives an unconfirmed cleanup result, while the runner retains the process handles, completion task and capacity slot. A completion callback observes deferred faults, disposes handles only after termination/readers finish, and releases capacity. Permanently retained pipes can exhaust all eight slots; this is explicit unavailability, not a successful release or permission to retry an effect. New runner diagnostics include the root PID, completion flags for root exit, each reader and termination, or exception type, not command arguments, captured output or exception bodies.

Process-tree termination is best effort. The [Process.Kill contract](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill?view=net-10.0) distinguishes root exit from descendant exit. This increment adds no OS sandbox, job object, persisted process supervisor, hard real-time deadline or crash recovery for child processes. Host loss cannot prove that an external effect did not occur. Operator investigation remains necessary for unknown outcomes and escaped descendants.

## Results and preserved governance

| Condition | CLI result | Workspace command result |
| --- | --- | --- |
| Complete capture and exit zero | Success with bounded stdout/stderr | Returns stdout |
| Nonzero exit | Existing unsuccessful result with bounded output | Existing exit-code exception |
| Output exceeds either limit | `process-output-limit`; no captured content | Existing output-limit exception |
| Eight slots occupied | `process-capacity-exhausted`; no child starts | Capacity exception |
| Cleanup deadline expires | `process-cleanup-unconfirmed`; no success | Cleanup timeout exception |
| Process startup or I/O raises an execution error | `process-execution-unconfirmed`; generic message without exception body | Existing exception category propagates |
| Caller cancels and cleanup completes | Caller cancellation propagates | Caller cancellation propagates |

ToolActor's exact authority, mandatory journal admission, leak scanning, approval behavior and duplicate protection are preserved. An admitted CLI invocation's unsuccessful or cancelled dispatch remains `OutcomeUnknown` under the existing journal semantics. Output rejection or child termination does not prove that a previous external effect did not happen. No automatic replay or new execution grant is introduced.

`ProcessCommandRunner` and `CliToolConnector` now require an `IProcessRunner` constructor argument. Manual consumers must compose that dependency explicitly; no parameterless fallback or alternate process execution path is retained. There is no persisted-state, actor-key, field-ID or external invocation protocol migration.

## Verification boundary

Three initial real-process regressions failed on the original CLI connector: excess stdout/stderr returned success, and cancellation left the actual child alive. Heavy parallel PowerShell startup then prevented two new fixtures from reaching their PID handshake. The final fixture is a Node `.ts` worker using only built-in modules; it preserves the same assertions, eight-process load and deadlines. Node 22.18+ is needed for these tests, with no npm install. CI selects the repository's existing Node 24 runtime policy.

Tests cover both stream limits, simultaneous streams larger than pipe capacity, dedicated reader identity and read faults, cancellation before dispatch, cancellation after an actual descendant starts, immediate output-limit termination, full capacity/no dispatch, capacity restoration, and a detached descendant holding pipes after root exit under a controlled clock. A real Host/Orleans/SQLite test performs an external file effect before overflowing output, preserves the unknown attempt across two Host instances and verifies the duplicate ID cannot repeat that effect.

The PR records final commands, tested commit, prerequisites and skipped checks. Repository checklist review is self-review. The tests prove the stated process and journal paths, not arbitrary CLI business success, isolation or descendant fencing.
