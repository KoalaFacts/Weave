# Durable tool invocations — 2026-09-19

## Scope

This increment adds a mandatory durable admission boundary to the existing
`ToolActor.InvokeAsync` path, plus outcome lookup through
`IToolActor.GetInvocationAsync`. It builds on the exact-operation grants in
PR #97. It does not add a second executor or a public HTTP/MCP ingress.

The supported persistence scope is **one host using one preserved on-disk SQLite
journal**. This is not a distributed journal for independently stored silo files.
The existing host still has other runtime/storage modes; their presence does not
establish cluster-wide invocation deduplication. Do not use separate per-node
files as a distributed execution guarantee.

No new package, project, workflow, approval engine, scheduler, or model account is
required. The SQLite implementation reuses `Weave.Security.Sqlite` and its
existing Microsoft.Data.Sqlite dependency. Its interface is owned by Invocations.

## Execution boundary

1. Establish the current workspace/tool identity, copy caller input and grants,
   normalize the adapter's actual operation, and validate its exact grant.
2. Preserve outbound leak checks, fingerprint the owned pre-substitution input,
   resolve existing secret references, and revalidate authority/connection state.
3. Atomically insert the invocation intent, local authorization evidence, and a
   distinct attempt into SQLite. A storage error prevents connector dispatch.
4. Revalidate after journal I/O too. Cancellation or denial at this point prevents
   dispatch. An existing invocation returns metadata only and cannot run again.
5. Call the existing connector once from this boundary. Record a confirmed outcome
   or preserve `OutcomeUnknown`. A completion-write failure never means that the
   external effect did not happen.

The SQLite transaction does not span the connector call. Intent and attempt
insertion share a transaction; a failed attempt insert rolls back the intent.
Completion conditionally updates only the exact, not-yet-completed attempt.
It does not overwrite earlier evidence or another attempt's result.

There is one attempt per invocation in this increment. The IDs are separate types
so a future reviewed recovery decision need not conflate a logical request with
an execution attempt. No automatic second attempt is implemented.

## IDs, duplicates, and input identity

A caller that needs response-loss recovery must create and retain its invocation
ID **before** submitting the request:

```csharp
var id = InvocationId.From(Guid.NewGuid().ToString("N"));
var request = new ToolInvocation
{
    InvocationId = id,
    ToolName = "files",
    Method = "write_file",
    Parameters = new() { ["path"] = "note.txt" },
    RawInput = "reviewed content"
};
var result = await tool.InvokeAsync(request, executionToken);
```

The ID is a nonzero 32-hex GUID, canonicalized to lowercase. IDs are unique within
a workspace, not merely within one tool. The existing signed token subject,
workspace, tool, connector type, normalized operation, ordinal-sorted parameters,
and raw input are bound into a versioned SHA-256 digest. Null and empty raw input
remain distinct; dictionary insertion order does not change identity.

Reusing an ID with changed subject, tool, operation or meaningful input is rejected
without another dispatch. Reusing the same ID/input returns the stored attempt's
metadata, including when it is still unconfirmed. Replayed success has
`IsReplay=true` and empty `Output`; it is not a new successful execution and does
not restore the original response body.

Omitting the ID generates a new one for each call for existing callers. That
preserves existing call sites but **does not provide response-loss deduplication**:
a caller that never received the generated ID cannot discover it through an
implemented listing endpoint. Two different IDs can execute the same operation
twice. Upstream retries/internal effects inside an adapter are not controlled by
this local admission mechanism.

The fingerprint is not the target approved-plan representation. It does not bind
a generalized resource/account, plugin installation, contract revision or rotated
secret value. The existing connection-version check remains local to the actor.
No approval or installation-revision safety claim follows from this digest.

## Outcome semantics

| Situation | Recorded or returned outcome |
| --- | --- |
| Intent/attempt cannot be persisted | `NotDispatched`; connector is not called. |
| New intent committed, no confirmed completion | `OutcomeUnknown`, with no completion timestamp. This can be running or interrupted. |
| Authority/cancellation fails after admission, before dispatch | `Denied` or `Cancelled` when that outcome can be recorded; no connector call. |
| Connector confirms success and completion write succeeds | `Succeeded`, with `OutcomeRecorded=true`. |
| Trusted adapter explicitly confirms failure | `Failed`; this still does not assert that no partial effect happened. |
| Exception, timeout or cancellation after entering dispatch | `OutcomeUnknown`; no automatic replay. |
| Legacy adapter returns unclassified `Success=false` | Conservatively `OutcomeUnknown`, not failure-with-no-effect. |
| Tool returns but completion cannot be written | `OutcomeUnknown`, `OutcomeRecorded=false`, and a safe `outcome-not-recorded` error. |

`OutcomeRecorded` means the reported outcome was durably recorded, not that an
unknown external outcome has been resolved. A missing completion timestamp is
also not proof of non-execution. Even a crash between committing intent and
starting the connector remains conservatively non-replayable. Recovery may need
upstream reconciliation or an explicit operator decision; neither is implemented
here. Do not retry an unknown result under a fresh ID.

Completion recording uses service-owned database I/O independent of the caller's
cancellation token. SQLite busy/lock waiting uses a five-second default timeout;
this is not a hard deadline for every possible filesystem stall. The original
completion event remains best-effort telemetry, not the durable record or an
outbox. Notification errors are logged without discarding the committed outcome.

## Query boundary

```csharp
var record = await tool.GetInvocationAsync(id, queryToken);
```

The query token needs `invocation:read`, a valid signature, a live expiry and
revocation state, the same workspace, and the original invocation's `IssuedTo`.
The actor's tool identity must also match. Access is checked before reading and
again after journal I/O. A different subject/tool cannot read the record merely
by learning its ID. The tool need not be connected, so a fresh Host can query a
preserved journal before recreating a connection.

This method is available through the existing .NET/Orleans actor contract only.
The execution-token narrowing in the tool registry does not silently add
`invocation:read`; a trusted host must separately issue reviewed query authority.
There is no new administrator export, dashboard, listing, or public HTTP endpoint.
This is not a completed external-agent identity/authentication flow.

## Storage and data handling

The default journal file is `~/.weave/invocations.db`. Override it through
`Weave:Invocations:DatabasePath` or environment variable
`Weave__Invocations__DatabasePath`. Use an absolute path on persistent local
storage for deployments. An explicit empty path, `:memory:` or SQLite URI path is
rejected. Journal initialization is eager: missing/unusable mandatory storage
blocks host startup instead of falling back to memory. Existing actor-state and
capability-audit storage settings are separate and do not disable this journal.

The provider requires WAL mode, uses FULL synchronous mode on each connection,
and enables foreign keys. It opens short-lived, non-pooled connections. Preserve
the database and its required WAL state using a consistent SQLite backup process;
copying or deleting arbitrary live files is not a safe reset/recovery procedure.
Changing the file path, losing the disk, restoring a stale backup or deleting rows
can remove duplicate protection. No automatic pruning is included for that reason.
Storage growth, permissions and backups remain operator responsibilities.

Only IDs, subject/tool/operation, the input digest, token ID, exact authorized
grant, timestamps, outcome and duration are stored. Raw request bodies, parameter
values, response bodies, token signatures, secret values and upstream exception
messages are not stored by the journal. An input digest is not encryption and can
still disclose equality or permit guessing low-entropy input; identifiers and
local metadata remain sensitive. This is not an encrypted/tamper-proof audit log.

Existing adapters are trusted in-process code. Existing shared connector-state
and failed-output-redaction limitations are not fixed or hidden by this journal.
It does not govern independent upstream credentials, native connector calls that
bypass ToolActor, or all workspace/connection lifecycle effects.

## Compatibility and validation

`ToolActor` now requires an `IInvocationJournal` and `TimeProvider`; there is no
production no-op/memory overload. `IToolActor` gains the outcome query, and request
and result contracts gain correlation/outcome fields. Custom hosts, actor bridges
and consumers must rebuild and explicitly compose durable storage. Coordinate a
restart; mixed-version rolling compatibility is not claimed. Existing actor keys
and persisted Orleans field IDs are not renumbered. Do not reset existing data.

The test-first baseline commit `a5a97f4f26680de79c726ff7326d766e6efd3638`
compiled and ran the unchanged implementation with three new real-file cases.
CI `35444377532` reported 2,464 existing cases passed and three intended failures,
with zero skips/framework errors. They exposed missing ID propagation, duplicate
writes and changed-input acceptance. Additional boundary regressions cover
reauthorization, ID normalization and committed-result notification handling.

Tests exercise real SQLite transactions, injected SQL failures, independent
connections, actual file effects, and disposal/recreation of the complete Host.
The incomplete-attempt test models a crash window by reopening persisted intent;
it is not a SIGKILL, power-loss or distributed failover certification. Test-only
in-memory doubles are limited to focused unit tests.

Final exact-head full-suite, formatting, dependency and security results belong
in PR #98's verification record. Compile/setup failures are not behavioral red
evidence, and passing individual cases does not excuse framework cleanup errors.
No skipped tests, assertion weakening, audit suppression, or formatting exclusion
is part of this increment. Review is author self-review, not independent security
certification. No main merge, release, deployment or production-data mutation is
included.
