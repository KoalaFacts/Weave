# Verified readable approval review

## Purpose and boundary

This increment extends the existing approval and HTTP ingress rather than adding
another decision model. Before presenting a pending request to an operator, match
the retained request and currently registered target against its durable approval.
Only then return readable content with the exact plan digest.

It is a read-only review operation: no approval decision, grant, invocation attempt,
connector dispatch or request-body persistence is added. Existing authorization
audit events may still be emitted. This is a backend review API, not a human login
system, a complete operator UI, or proof that a person read the response.

## Request

Enable the existing `Weave:Invocations:Http:Enabled` profile and configure approval
requirements as described in [the ingress guide](2026-09-20-governed-http-entry.md)
and [the approval guide](2026-09-20-durable-approval.md).

```text
POST /api/workspaces/{workspaceId}/tools/{toolName}/invocations/{invocationId}/approval/review
X-Weave-Capability: <the independent reviewer's signed capability envelope>
Content-Type: application/json
```

Send the retained original invocation, not an invented summary or model-generated
paraphrase. The original caller retains this request because the journal does not
store request bodies. The route and body IDs must match.

```json
{
  "invocationId": "1a628f6d417b40a19a2ebf22725ab813",
  "toolName": "files",
  "method": "write_file",
  "parameters": { "path": "note.txt" },
  "rawInput": "Content proposed for review."
}
```

The capability subject must differ from the original requester and hold all of
`invocation:read`, `approval:decide`, and `tool:files:approve:write_file` in the
same workspace. No execution grant is needed just to review. Existing global API
authentication remains in force too. Do not give agents a shared administrator
credential or expose unrelated administrative routes.

## Verified response

A successful response is JSON containing the canonical invocation ID, original
workspace/subject, normalized tool operation, a registered-target description,
owned parameters, original raw input, plan digest and expiry. The target
information comes from the adapter's live registered configuration, not a
caller-supplied display label. The current FileSystem description includes its
absolute root, sandbox/read-only settings, read limit and binding version.

The content and target are matched before being returned. The response is marked
`Cache-Control: no-store`, `Pragma: no-cache` and `X-Content-Type-Options: nosniff`.
Default JSON escaping is retained. A review UI must render every field as text,
not HTML or Markdown instructions; escape control characters and visibly preserve
complete content, including whitespace. JSON escaping alone is not a safe UI.
Do not render a truncated preview as though it were the complete approved content.

This response is a snapshot, not a signed approval receipt, lock, or execution
authority. A caller can edit its local copy; the server does not trust that copy
as proof of review. The trusted operator surface must associate the content it
actually displayed with this response's exact plan digest and invocation ID.

After review, an operator still uses the existing separately authorized
`DecideApprovalAsync`. No new public decision endpoint is exposed here. Approval
does not execute: the original caller resubmits its retained request and ID through
`InvokeAsync` using current execution authority. Existing target/input/expiry
revalidation and atomic consumption remain authoritative. A changed or expired
plan cannot become valid merely because an older preview exists.

## Binding and confidentiality

The review uses the same v1 canonical input encoding as invocation admission. It
compares against the stored original subject rather than pretending the reviewer's
token is the requester's authority. Pure digest calculation is not authorization.
All actual read/review checks use the reviewer's validated token.

The current target binding includes the effective post-substitution input digest.
Review deliberately does not resolve secrets on the reviewer's behalf. Therefore
this first profile supports inputs whose effective form is unchanged. A plan that
resolved a secret-backed path/body cannot be shown as a verified original-only
preview: it returns `approval-review-unavailable`, without resolving or returning
that secret. A changed target can produce the same unavailable result because the
current stored target digest combines target and effective input; the API does
not invent a more precise cause it cannot establish.

Only adapters with a supported digest and plaintext target description can produce
a preview. The FileSystem extension supplies the first implementation. Description
and binding are rechecked before returning. In-process adapters are trusted;
these checks do not prove protection against hostile native filesystem mutation,
symlink races, external file changes, or a malicious adapter.

Review requires a live equivalent connection. A disconnected target cannot be
verified. Reconnecting the same configuration preserves the binding; changing its
root or safety settings does not. The review rechecks approval state, reviewer
authority, target and expiry after asynchronous work. Cancellation is propagated
through the host's explicit Orleans RPC cancellation argument rather than relying
on a serialized capability field.

## Failure behavior

| HTTP status | Meaning |
| --- | --- |
| 200 | Verified pending-plan content; no decision or execution occurred. |
| 400 | Invalid request, identity, or route/body invocation-ID mismatch. |
| 401 / 403 | Invalid credentials or insufficient/incorrect reviewer authority. |
| 404 | No approval in the permitted tool context, or ingress is disabled. |
| 409 | Changed input, non-pending state, or a target/effective input that cannot be safely previewed. |
| 413 / 415 | Request exceeds the byte limit, or media type/content encoding is unsupported. |

Conflicts return only a safe error code, never echo unverified private input or an
unverified target. The existing raw-body limit is shared with invocation submission
and applies with or without Content-Length. Unexpected storage failures are not
translated into a successful or fabricated preview.

## Implementation and verification

The slice lives under `src/Invocations/ReviewApproval`; transport stays in
`hosts/Weave.Host/Invocations/ReviewApproval`; target description stays in the
FileSystem extension. No new project, package, schema, state machine or background
worker. Custom IToolActor implementations must rebuild for the added review method.
An adapter that supplies no optional description remains usable for its prior
operations but cannot produce a readable review.

The first test-only commit `b051d8e07555213055bad0e74d40f5699926678c` ran in
CI 35475136517: thirteen new review cases failed because the route did not exist;
the disabled-route control passed. Six separately reproduced inherited failures
were fixed concurrently by #103 and retained via merge commit 9f703bde. They are
not counted as review-feature red evidence.

The first implementation e43915d9 passed all 2,630 cases, but had one whitespace
diagnostic; that run was not reported as complete CI success. Additional tests
cover repeated review, decided states, equivalent reconnects, dual authentication,
real RPC cancellation, secret-substitution rejection, and the unchanged canonical
fingerprint. The existing Host/Orleans/filesystem/SQLite fixtures are used; no live
model or external service credentials are required.

Final exact-commit results, skipped/error counters and artifact hashes belong in
PR #105's verification record. Review is author self-review, not independent
security certification. No main merge, release, deployment or data reset is
performed by this increment.
