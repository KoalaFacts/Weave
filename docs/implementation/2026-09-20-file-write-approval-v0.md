# File-write approval v0

## Scope and configuration

This increment adds a complete, deliberately narrow approval flow to the existing
ToolActor and SQLite invocation journal. It is opt-in through server configuration:

```json
{
  "Weave": {
    "Approvals": {
      "RequireFileWriteApproval": true,
      "Lifetime": "00:30:00"
    }
  }
}
```

The flag defaults to false, preserving existing deployments until configured.
The caller cannot override it in ToolInvocation. A positive lifetime of at most
24 hours is supported. Approval expires at the earlier of its original deadline
and the approving token's expiry. No automatic extension or approval renewal occurs.

When enabled, filesystem reads retain their ordinary exact-operation checks.
`write_file` supports one `path` parameter containing a single ASCII basename
(up to 128 characters), plus a non-null RawInput text body of at most 16 KiB UTF-8
and within the configured write limit. A sandboxed, writable, application-controlled
root is required. Configure an absolute root and do not change the process working
directory during a connection. Nested paths, symbolic/reparse destinations,
secret-substitution expressions and unsupported filesystem mutations, including
`edit_file`, are rejected rather than silently bypassing approval.

This is not approval support for CLI, MCP, HTTP, arbitrary paths or credential
provisioning. Do not grant alternate tools that can bypass a required deployment
boundary. This code does not isolate hostile native code or another OS process
that can mutate the filesystem. Link checks are revalidated before dispatch but
are not an atomic OS-level defense against concurrent link/hardlink replacement.

## One path, with explicit operations

The request must already have `tool:<name>:invoke:write_file`. Approval is an
additional obligation, never a way to obtain missing execution authority.

```csharp
var id = InvocationId.From(Guid.NewGuid().ToString("N"));
var pending = await tool.InvokeAsync(new ToolInvocation
{
    InvocationId = id,
    ToolName = "files",
    Method = "write_file",
    Parameters = new() { ["path"] = "note.txt" },
    RawInput = "The exact note the reviewer will see"
}, writerToken);

// An authenticated, separately authorized reviewer inspects the actual plan.
var review = await tool.GetApprovalAsync(id, reviewerToken);
if (review is not null)
{
    var decision = await tool.DecideApprovalAsync(id, review.PlanDigest,
        ApprovalDecision.Approve, reviewerToken);
    if (decision.Applied)
    {
        // Explicit resume uses the stored plan, not another caller-supplied body.
        var result = await tool.ResumeApprovedAsync(id, writerToken);
    }
}
```

These are the .NET/Orleans actor methods, not invented CLI commands or public HTTP
endpoints. The example assumes authorized tokens and a connected tool. It is not
a demonstration of production human authentication or a UI that this PR provides.

The proposer gets `approval-required`, an InvocationId and Pending status, with
**no AttemptId and no dispatched Outcome**. Waiting is neither execution nor
OutcomeUnknown. No request or transaction remains open waiting for a person.
The same request/ID returns the existing approval; changed input or target cannot
replace the plan. A generated ID is supported, but response-loss recovery requires
retaining an explicit caller-generated ID before the first submission.

The review plan binds that ID, original signed subject/workspace/tool, normalized
root and filename, exact text, effective write limit and the v0 operation revision.
Its digest is SHA-256 over the source-generated JSON representation of
FileWriteApprovalPlan (declared field order, no mutable dictionary fields). The
context explicitly registers the existing generated InvocationId converter so
independently generated ID metadata cannot serialize as an empty object. This is
an operation-specific representation, not a claim to implement the complete
Tenant/Room/resource/installation/credential model in ARCHITECTURE.md.

## Authority and state transitions

| Operation | Authority and conditions |
| --- | --- |
| Submit | Existing exact write grant, valid token, usable filesystem connection and supported plan. |
| Read review plan | `approval:read`; a non-requester additionally needs workspace-scoped `approval:decide`. |
| Approve/reject | `approval:decide` and the exact tool write grant; reviewer subject must differ from requester; expected plan digest must match. |
| Cancel | `approval:cancel`, original requester subject and matching plan digest; allowed while Pending or Approved. |
| Resume | Original requester with current write authority, unchanged target/configuration, valid approval and unrevoked reviewer token ID. |
| Read executed outcome | Existing `invocation:read` owner-scoped query, separate from review-plan access. |

Pending can become Approved, Rejected, Expired or Cancelled. Approved can become
Consumed, Expired or Cancelled. Rejection, cancellation and observed expiry do not
return to Pending. Cancellation preserves the original reviewer evidence.
Expired state is persisted on observation; no background expiry scheduler is added.

Approval records contain reviewer subject, token ID and decision time, but not
token signatures. Execution revalidates the requester, decision revocation,
expiry, plan and connection configuration before admission and again before
connector dispatch. Changing grant configuration does not itself revoke issued
tokens; explicit revocation/expiry rules from the existing token service still apply.

The server reconstructs execution input from the protected stored plan. Resume
accepts no replacement parameters and performs no deferred secret substitution.
A changed root, read-only/sandbox setting or effective write limit invalidates the
approval. A policy-disabled host cannot resume an old plan, and an existing
approval's ID cannot pass ordinary journal admission without its approval proof.

## Atomic admission, restart and recovery

The existing journal and approval store are two interfaces on the same SQLite
instance. A short write transaction validates and changes Approved to Consumed,
then inserts invocation intent, authorization evidence and the execution attempt.
Failure rolls back all of these writes. The transaction ends before tool dispatch.
Independent connections cannot consume the same approval for separate attempts.

Repeated resume returns existing outcome metadata without redispatch. A crash,
transport failure or lost result after admission keeps the existing conservative
OutcomeUnknown semantics. Consumed does not itself mean the file was written:
it means that approval was used to admit an attempt. Cancellation or revocation
after an effect starts cannot undo the effect. There is no blind replay, automatic
second attempt, external reconciliation or exactly-once claim for upstream systems.

Pending plans and decisions survive complete Host disposal/recreation against the
same retained journal and protection keys. Actor consumers must rebuild for the
new methods and mandatory FileWriteApprovalService composition. No old tables or
persisted actor IDs are renumbered or reset; the approval table is additive.
This is still a single-host/preserved-database boundary, not independent silo files
acting as a distributed journal.

## Plan confidentiality and operational responsibilities

Unlike the metadata-only execution journal, resume needs the exact pending text.
It is persisted as an authenticated protected blob using ASP.NET Data Protection,
with a purpose bound to workspace and invocation ID. Authorized plan queries
return the clear text for review; logs and execution journal rows do not store it.
Known leak scanning runs before admission, and dynamic secret substitution is not
supported for approved notes. This is not a guarantee that arbitrary sensitive
text can always be classified automatically.

The key ring is stored beside the journal at `<database-path>.approval-keys`.
Keep both on durable storage with access restricted to the host identity, and
back them up consistently. Explicit filesystem key-ring persistence does not
itself encrypt those key files at rest. Reading the journal AND key ring permits
plan decryption; this is not protection from the host administrator or a compromised
host. Key loss/tampering fails recovery rather than replacing the approved content.
No production encryption keys are generated, rotated or uploaded by this task.

The audit metadata is not cryptographically tamper-proof. Retention, disk capacity,
backups, review-UI confidentiality and trusted host/token issuance remain deployment
responsibilities. A test reviewer with a signed token is not proof of a complete
human identity system. Built-in API auth remains unchanged.

## Verification

The test-only baseline `ce73a7a85136268eeb4df7e4a57f5b3acea9df71` ran against
unchanged production: the two expected admission cases modified the real file and
failed, while all 2,522 existing cases passed. This is behavioral red evidence,
not a compilation failure. The subsequent fixture initialization compilation error
was corrected without changing the immutable production token-options contract.

The tests exercise real Host/Orleans/filesystem behavior, protected-plan recovery
across Host restarts, unauthorized decisions, immutable plan/target checks,
rejection/cancellation/expiry, SQLite failure injection and independent database
workers. Existing leak, operation-authority and journal assertions are retained.
Exact final commit, runs, test counts and any remaining failures are recorded in
PR #104; this document does not turn an unverified build into a passing result.
