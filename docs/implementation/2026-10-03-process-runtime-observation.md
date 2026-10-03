# Command process runtime observation

## Operator path

On a Host configured for [trusted operator mode](2026-09-22-trusted-operator-onboarding.md),
GET `/api/operator/runtime/processes` returns a read-only snapshot of the shared
finite command runner. Supply `X-Weave-Operator-Key` using the same protected
transport and credential handling as the existing operator routes. If global API
authentication is enabled, it is an additional requirement.

The endpoint is absent when operator mode is disabled, including Agent-only mode.
An Agent capability does not authorize this query. This is a Host-wide operator
view; it does not filter by submitted workspace identifiers. Bodies and query
parameters are rejected. Responses use the operator middleware's `no-store` headers.

The runner is shared by CLI tool invocations and workspace container-engine
commands. Protocol workers, background Host launchers, arbitrary descendant
processes and other Hosts are outside this snapshot.

## Read the snapshot

| Field | Meaning |
| --- | --- |
| `observedAt` | Observation time from the injected clock. |
| `capacity` / `available` | Eight execution slots per runner, minus all admitted executions, including retained cleanup. |
| `executionId` | Opaque identifier for this admission; it is not an invocation ID or an authorization credential. |
| `admittedAt` / `elapsed` | Admission time and elapsed duration measured using the clock's monotonic timestamps. |
| `phase` | `Starting`, `Running`, `CleaningUp`, or `CleanupUnconfirmed`. |
| `exit`, `standardOutput`, `standardError`, `termination` | Completion of each owned task: `NotStarted`, `Pending`, `Completed`, or `Faulted`. |

For example, an execution with `phase=CleanupUnconfirmed`, `exit=Completed` and
`standardOutput=Pending` has returned an unconfirmed cleanup result to its caller,
but the stdout reader still has not finished. A descendant may be holding the pipe.
Its slot remains occupied until termination and both readers have completed.
The cleanup-deadline warning includes the same `executionId`, allowing an operator
to correlate the snapshot with the existing process PID and completion flags in logs.
`available=0` explains immediate capacity rejection; it is not evidence that any
previous external effect failed.

Admission, removal and snapshot projection use one synchronization boundary.
Returned snapshots are immutable and bounded to eight entries. A failed start or
confirmed cleanup removes the admission. Deferred cleanup keeps the same identity
and releases capacity only when its owned tasks finish. Observing does not kill,
dispose, queue or rerun a command.

The response includes no executable, PID, arguments, working directory, captured
output, exception body, tool identity, workspace identity or credentials. Task
completion is a process diagnostic, not the business result or durable invocation
outcome. A faulted task retains its normal error propagation; querying it does not
convert failure into success.

## Preserved behavior and limits

The [finite process lifecycle](2026-10-03-process-lifecycle-hardening.md) still owns
output bounds, dedicated readers, cancellation, best-effort tree termination and
the independent five-second cleanup deadline. Exact operation grants, approval
binding and durable attempt admission remain unchanged. Investigate retained
cleanup together with the invocation or management journal; do not blindly retry
an unknown effect or release its capacity while readers remain live.

The internal channel and separate unfinished-task dictionary are replaced by one
bounded admission registry, making capacity and observation describe the same
executions. `IProcessRunner.RunAsync` and its consumer result categories are unchanged.
The Host composes execution and observation interfaces from the same singleton.
There is no persisted-state, actor-key, field-ID or invocation-protocol migration.

This registry exists only in the running Host. A restart discards these diagnostic
identifiers and cannot prove that children exited or effects did not occur. This
increment adds no process recovery, OS sandbox, descendant fencing, force-release
or termination API. Permanently retained pipes can still exhaust capacity.

## Verification scope

The HTTP regression first returned 404 on the original implementation. After
adding the empty snapshot path, a real running-child observation failed because
it contained no admission. Regressions now cover live execution, eight-slot
exhaustion without dispatch, immutable snapshots, controlled elapsed time,
startup failure, concurrent observation during cancellation, held-pipe cleanup
uncertainty, warning correlation and eventual release.

Host tests query the actual singleton while a real Node child is running and
confirm that command data is not returned. HTTP checks cover operator and Agent
credentials, global authentication, disabled routes, spoofed transport headers,
input rejection and noncacheable successful responses. Node 22.18+ is required;
the worker uses only built-in modules and needs no npm install. The PR records
the final commands, tested commit, prerequisites and skips. Checklist review is
self-review, not an independent reviewer.

The first full Windows run exposed an existing PID-handshake sharing failure in
the eight-child fixture. A deterministic real-child regression holds a shared
publisher handle open; the original `File.ReadAllTextAsync` path fails with the
same sharing violation. The helper now explicitly permits read/write/delete
sharing while reading the already published PID, matching the
[FileShare contract](https://learn.microsoft.com/en-us/dotnet/api/system.io.fileshare?view=net-10.0).
It retains the original deadline, PID assertions, eight-process load and error
propagation, with no I/O retry or exception suppression. The identity of the
handle holder in the original failing run was not captured.

A later full run timed out waiting for the existing Python HTTP close fixture's
readiness message. A dedicated regression confirmed that its event-based pipe
reader handled readiness on a shared worker thread. That fixture now uses owned
dedicated stdout/stderr readers, observes reader faults and waits for both drains
during cleanup. Its startup and HTTP deadlines, positive/negative close controls,
post counts and no-replay assertions remain unchanged. The timed-out run was
stopped after the known fixture failure; it is not counted as a completed pass.
