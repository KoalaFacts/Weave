# M1 increment: durable approval before execution

## Scope

This increment builds on main `02db852d59f4e994d74a950bfa76414d9dedd87d`.
It adds an approval gate to the existing ToolActor/InvocationExecution path, not
another executor or a universal workflow engine. The first target-binding adapter
is FileSystem. PR #101's independent Direct HTTP isolation work is not modified.

The operator API is the existing in-process/Orleans tool actor interface. This is
not yet a public HTTP/MCP approval ingress, human identity provider, CLI approval
command, notification channel, or approval UI. A signed, independently authorized
principal acts as the approver; the code does not infer that every such principal
is a human. Trusted administrators remain responsible for issuing these grants.

## Configuration and authority

Configure the host's existing invocation journal options:

```json
{
  "Weave": {
    "Invocations": {
      "DatabasePath": "/var/lib/weave/invocations.db",
      "ApprovalRequiredGrants": ["tool:files:invoke:write_file"],
      "ApprovalLifetime": "00:30:00"
    }
  }
}
```

The default requirements list is empty: existing invocations do not acquire an
implicit approval requirement merely by upgrading. Requirements are validated and
snapshotted at startup. Invalid scopes/expiry configuration reject startup, not
silently disable approval. Existing pending requirements survive removing the
matching policy and restarting. Lifetime must be from one second to seven days.

Permission and approval are independent. The requestor still needs the exact
`tool:files:invoke:write_file` grant. Approval does not add that grant. The decision
principal requires BOTH `approval:decide` and `tool:files:approve:write_file` in the
same workspace. The original subject cannot approve/reject its own request, even
with those grants. A requestor with `invocation:cancel` may cancel its own pending
or approved-but-unconsumed request. Cancellation cannot undo a consumed attempt.

`GetApprovalAsync` requires `invocation:read`; another subject additionally needs
the global decision and exact tool-operation approval grants. Workspace/tool
identity is checked independently. It works on an activated actor without a live
tool connection, allowing inspection and decision after a host restart.

## Workflow

Keep the explicit invocation ID and original request before submission. A waiting
response has `ErrorCode=approval-pending`, an approval plan digest/expiry, and no
AttemptId. It is neither successful execution nor an unknown dispatched outcome.

1. Submit the authorized request through `IToolActor.InvokeAsync`.
2. Read its approval metadata with `GetApprovalAsync` under the appropriate token.
3. Review the original request and target through a trusted channel. The persisted
   metadata is not a readable request-body cache; do not approve an unexplained
   opaque digest. A subsequent UI must bind its reviewed content to this digest.
4. The independent operator calls `DecideApprovalAsync(id, planDigest, Approve, token)`.
5. The requestor resubmits the SAME ID and original request using current authority.
   Approval itself never dispatches, and no old execution token is saved for later.
6. Query the committed invocation outcome through the existing GetInvocationAsync.

Rejected, expired and cancelled requests remain terminal for that ID. Changing
input/subject/operation/target under a pending ID is a conflict, not a replacement.
Identical repeated decisions are idempotent; they do not dispatch or duplicate the
committed decision event. Concurrent opposite decisions have one winner.

## Plan binding and ownership

The generic invocation feature knows only IApprovalTargetBinding, an optional
trusted-adapter contribution. The FileSystem extension hashes the registered
absolute root, read-only flag, read limit and its versioned dispatch contract.
Equivalent reconnects produce the same target binding. Display names and transient
connection IDs are not used as durable target identity.

The plan also binds workspace, original subject, invocation ID, normalized
operation, original input digest, effective post-substitution input digest,
request time and expiry. Raw request/response bodies, actual credentials, token
signatures and upstream exception text are not approval journal columns.
Changing a resolved secret-backed input invalidates the prior binding.

An adapter without a reliable target contribution is rejected when a configured
operation requires approval; it is not treated as implicitly approved. In-process
adapters remain trusted and can misrepresent semantics. This binding is not full
Tenant/Room/installation/schema-revision infrastructure, filesystem inode/content
versioning, or protection against privileged external filesystem changes.

## Atomic admission, outcomes and recovery

Pending approval lives in invocation_approvals with no invocation_attempts row.
Decision state and an append-only invocation_approval_decisions row commit in one
short SQLite transaction. Approved-plan consumption, invocation intent and the
single execution attempt commit atomically in the existing journal transaction.
A failed attempt insert rolls back consumption. Recording failure blocks dispatch.

After claim I/O, the current requestor's authority, cancellation, connection/target
and approval expiry are checked again. A failed post-claim check leaves a recorded
non-dispatched/denied attempt; it does not automatically replenish approval.
Once consumed, duplicate IDs never cause another dispatch. Unconfirmed external
results retain the existing OutcomeUnknown and no-blind-replay behavior.

No database transaction, request thread or token remains open across a human
choice. Complete host recreation retains waiting/decision state only when the same
on-disk SQLite journal is preserved. Separate per-node databases are not a shared
journal. This is not exactly-once execution of arbitrary upstream services.
Authorization checks cannot be an atomic transaction with an external revocation
source or already-started effect; cancellation/revocation cannot undo that effect.

Schema changes only add approval tables; existing invocation rows and field IDs
are not reset. Rebuild custom IToolActor implementers/hosts for the new methods.
Back up and protect the complete journal. Digests are unkeyed correlation/binding
evidence, not encryption or proof against offline guesses or journal tampering.

## Verification record

The corrected test-only commit `c358e5c192b66e9164843cf77b1623040c7da4d7`
compiled and exposed two intended failures in CI `35450187171`: 2,524 passed,
2 failed, zero skipped/framework errors. The first test observed an immediate
write instead of waiting. Artifact 10586569389 was downloaded and its SHA256
`2e9df4419345db8c660cb1ee3b9bdcbc8bae896f06cded725893b9080ebc2ad5` checked.
An earlier analyzer failure is not counted as behavioral red evidence.

Concurrent test additions through ac675d7 were retained verbatim. New tests cover
real filesystem effects, restart, exact decisions, rejection/cancel/expiry,
changed target/input, revocation, immutable policy configuration, SQLite concurrent
claims and transactional rollback. Final exact-commit results belong in PR #102;
this document is not a substitute for the current CI result. No warning/audit
suppression, new package, alternate test framework, or relaxed formatting gate is
introduced. Review is author self-review, not independent security certification.
