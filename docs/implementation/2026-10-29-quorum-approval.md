# Quorum (multi-person) approval

## Scope

This increment lets an operator require more than one distinct human approver
for selected approval-required operations. It builds on
[human-only approval decisions](2026-10-29-human-approval-principals.md) and the
[durable approval journal](2026-09-20-durable-approval.md). It adds no identity
provider, notification channel, review UI or approval link.

## Configuration

Quorums are a list (configuration keys cannot contain the `:` in grant scopes):

```json
"Weave": {
  "Invocations": {
    "ApprovalRequiredGrants": ["tool:mail:invoke:send", "tool:files:invoke:write_file"],
    "ApprovalQuorums": [
      { "Grant": "tool:mail:invoke:send", "RequiredApprovals": 2 }
    ]
  }
}
```

- `Grant` must be an exact, distinct entry of `ApprovalRequiredGrants`; a quorum
  never makes an operation approval-required on its own.
- `RequiredApprovals` is 1 to 3. Unlisted required grants need one approver.
- Startup rejects invalid entries. The policy is snapshotted at construction.

## Semantics

- The quorum is resolved when approval is requested and stored on the approval
  row (`required_approvals`). Lowering or removing a quorum later does not lower
  the requirement for approvals already waiting, matching the existing rule that
  removing a requirement does not bypass a stored approval.
- Every approver must still be a Human token holding the exact
  `tool:<name>:approve:<operation>` grant, and the requester never counts.
- Each approval from a distinct human is appended to
  `invocation_approval_decisions`. The plan stays `Pending` until the number of
  distinct approvers reaches the stored quorum, then becomes `Approved` in the
  same transaction as the final vote.
- A repeat approval by the same human is idempotent and counts once.
- One rejection while pending rejects the plan, regardless of earlier approvals.
  Cancellation by the requester is unchanged.
- `decided_by`, `decision_token_id` and `decided_at` describe the final state
  change only; the decision table keeps every vote.
- `InvocationApproval` exposes `RequiredApprovals` and `Approvals` (distinct
  approvers so far). The HTTP approval status adds the same two fields.

## Migration

Opening a version-1 journal adds `required_approvals INTEGER NOT NULL DEFAULT 1
CHECK(required_approvals BETWEEN 1 AND 3)` to `invocation_approvals` if it is
missing and sets `PRAGMA user_version=2`, including under
`RequireExistingStorage`. Existing rows keep single-person approval. No
historical decision is rewritten. A version-2 journal whose quorum column is
missing fails to open rather than silently defaulting to one approver; any other
version fails closed.

### Rollback

Binaries before this change accept only `user_version=1`
(`EnsureProposalSchema`), so after the upgrade they refuse to open the journal
instead of applying single-approval logic to a waiting multi-person plan. A
rollback therefore needs either this binary or an explicit, reviewed downgrade
of the journal: settle or cancel every pending approval with
`required_approvals > 1`, then reset the version. Do not reset the version
while such plans are pending.

## Not covered

- No guarantee that a quorum is satisfiable: the operator must issue enough
  distinct Human approval tokens.
- Approvers are distinguished by token subject (`IssuedTo`). Issuing two tokens
  with different subjects to one person defeats the quorum; subject issuance is
  the operator's responsibility.
- Per-approver roles (for example "one finance and one security approver") are
  out of scope.

## Verification

- `InvocationApprovalPolicyTests`: invalid quorum, duplicate quorum, snapshot.
- `QuorumApprovalFlowTests`: approval only at quorum, repeat approver counted
  once, partial approval then reject, requester excluded, stored quorum kept
  across restart after lowering, three-person quorum, upgrade of a version-1
  journal (with and without `RequireExistingStorage`), fail-closed open of a
  version-2 journal missing the column and of an unknown later version.
- Concurrent votes are serialized by the existing immediate (write-locking)
  SQLite transaction around read, count and write; no separate race test was
  added for duplicate or approve-versus-reject races.
- Existing durable-approval suites unchanged and passing.
