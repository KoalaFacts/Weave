# Reviewed approval decisions and operator example

This increment extends the canonical verified review and durable approval path.
It adds an opt-in operator decision endpoint and a Python standard-library
terminal helper. It does not add another approval store, execution route, schema,
workflow engine, identity provider, or package dependency.

## Enable the operator endpoint

Both switches must be enabled in the Host configuration:

```json
{
  "Weave": {
    "Invocations": {
      "Http": {
        "Enabled": true,
        "DecisionsEnabled": true
      },
      "ApprovalRequiredGrants": ["tool:files:invoke:write_file"],
      "ApprovalLifetime": "00:30:00"
    }
  }
}
```

`DecisionsEnabled` is false when absent. The existing public-development-key
rejection still applies to the governed HTTP profile. Supply properly protected
signing material, persist the journal outside agent-accessible roots, configure
transport protection and any required global API authentication separately.
These switches do not safely expose the Host's unrelated administrative routes.
An administrator must already have configured the tool and issued narrow tokens;
this example does not provision them or grant itself authority.

## Decision contract

`POST /api/workspaces/{workspaceId}/tools/{toolName}/invocations/{id}/approval/decision`
accepts this envelope:

```json
{
  "invocation": {
    "invocationId": "14bd2b92c8a34fdd8c79cb62eab32a13",
    "toolName": "files",
    "method": "write_file",
    "parameters": {"path": "note.txt"},
    "rawInput": "The exact originally submitted content"
  },
  "planDigest": "approval-v1:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
  "decision": "approve"
}
```

The digest above is an illustrative placeholder, not a valid approval. Use the
actual digest returned by the verified review of that retained invocation.
Only the exact lowercase actions `approve` and `reject` are accepted. Duplicate
object properties and unknown typed fields are rejected, including extra commands
such as `executeImmediately`. Do not serialize internal ToolInvocation metadata
such as `parseWarning` into this public decision envelope.

Supply exactly one `X-Weave-Capability` header using the existing base64url signed
capability format. Authentication occurs before body parsing or actor lookup.
The decision principal must differ from the requestor and have `invocation:read`,
`approval:decide`, and the exact `tool:<name>:approve:<operation>` scope. Existing
global API authentication is an additional gate, not a substitute for this token.

The endpoint calls the feature-owned `DecideReviewedApprovalAsync` through a native
Orleans cancellation-bearing RPC. That operation re-runs verified review using
the original content and current registered target, compares the exact supplied
digest, then invokes the existing authorized durable decision operation using
the verified snapshot's ID. A client-supplied preview or guessed digest is not
accepted instead of original-content validation. No secret is resolved for the
reviewer, and the inherited review limits for substituted inputs still apply.

An approved decision records no execution attempt and causes no tool effect.
Only the original caller's separate resubmission can execute, under its current
execution authority and unchanged valid plan. Rejection is not a temporary wait.
A caller cannot acquire a missing execution grant by obtaining approval.

### Response and retry semantics

| Response | Meaning |
| --- | --- |
| 200 | Decision recorded; body contains invocationId, approvalState and expiresAt. No execution was requested. |
| 400 | Invalid ID/body/action, ambiguous JSON or unsupported fields. |
| 401 / 403 | Invalid credentials or insufficient/incorrect principal authority. |
| 404 | Route disabled or approval not visible/found under the requested context. |
| 409 | Content/target/digest conflict, expiry or no longer Pending. Query status; do not turn it into a new execution ID. |
| 413 / 415 | Body too large or unsupported content representation. |
| 500 | Unconfirmed operation; internal error detail is not returned. Query status before any deliberate retry. |

Raw input is bounded to 1,048,576 bytes even without Content-Length. Responses are
no-store; successful decision responses omit original content and token evidence.
A repeated HTTP confirmation returns 409 `approval-not-pending`, preserving the
first decision; it is not another approval or another execution. The original
backend decision API retains its existing idempotent behavior. Opposite concurrent
decisions have one winner. Actual decision-history insertion failure rolls back
both state and history. However, a dropped response can follow a successful commit:
never infer from a transport error that a decision did not occur.

## Terminal operator flow

[The example](../../examples/governed-tools/README.md) uses only Python 3.10+
standard-library modules. Supply the retained invocation file, server URL and
workspace. The reviewer capability is read from a hidden prompt or the
`WEAVE_REVIEW_CAPABILITY` environment variable; an optional global operator bearer
uses `WEAVE_OPERATOR_BEARER`. Neither is a command-line argument. Environment values
still need protection from same-user processes, diagnostic dumps and CI logs.

The helper requests verified review, checks response identity/content against its
owned original snapshot, prints the complete response as escaped JSON, and requires
`approve <full-plan-digest>` or `reject <full-plan-digest>`. Blank input and generic
`yes` send no decision. It has no auto-approve switch and no tool-execution call.
It escapes control, bidirectional and non-ASCII characters rather than interpreting
terminal control sequences or HTML. The display is deliberately textual; it is not
a rich browser review screen, human identity proof or an automatic safety judgment.

Only HTTPS is accepted outside literal loopback. Environment proxies and HTTP
redirects are disabled to avoid forwarding the capability. Requests/responses are
bounded, a transport timeout is set, and no failed request is automatically retried.
The helper's output contains reviewed content and must itself be treated as private.
A review is point-in-time validation, not a lock or signed approval receipt. The
server repeats validation at confirmation and the existing execution path repeats
its checks before dispatch. Cancellation is cooperative and cannot undo a decision
or external effect that has already committed.

## Verification scope

Real Host/Orleans/SQLite/filesystem tests cover preview/approve/reject, original
caller resumption, changed content/digest/target, self/insufficient authority,
credential failures, duplicate decisions, concurrent opposite decisions, rollback,
body limits and default-disabled configuration. A gated authorizer verifies native
HTTP cancellation inside the actual grain while a real approval remains pending.
Controlled-clock tests cover expiry; revoked/cancelled review cannot create a
decision. A separate Python process runs the actual example against loopback Kestrel
for approve, reject and leave-unchanged cases, with global authentication enabled.
No production credentials or real upstream service are involved.

The Python unit suite checks immutable confirmation payloads, wrong confirmations,
preview mismatch, escape rendering, duplicate JSON, origin validation, no retries,
and a real local redirect/proxy test. Existing CI discovers these tests alongside
its repository checks. Exact commit/run/artifact evidence is recorded in the PR;
passing compilation is not a claim of Native AOT publication or penetration testing.

The change adds no database migration or data reset. Custom IToolActor adapters
must rebuild for the added method. Preserve prior filesystem-v2 binding and all
retained invocation IDs, particularly unknown outcomes. Durability remains one
preserved SQLite journal, not a distributed exactly-once guarantee.
