# Governed invocation HTTP entry

## Delivered boundary

This increment adapts the existing `IToolActor` invocation, outcome lookup and
approval-status lookup to HTTP. It does not call a connector or modify the journal
directly. The approval backend is the prerequisite delivered in PR #102.

No public token issuer, tool-connection mutation, permission management, approval
decision endpoint, review UI or MCP server is added. An administrator must already
have connected the tool and provisioned an appropriately scoped signed token.
This is a developer integration boundary, not yet a self-service onboarding flow.

## Opt in without opening the administration surface

Set `Weave:Invocations:Http:Enabled=true`. Otherwise these routes are not mapped.
The enabled profile rejects the repository's public development signing key in
both `CapabilityTokens:SigningKey` and `CapabilityTokens:PreviousSigningKey`.
The token service still enforces its own configuration requirements. Choose and
protect a real signing key outside source/configuration committed to Git; the
known-key check is not an entropy test or a general key-management system.

The existing global API authentication is unchanged. When enabled, it remains
an additional check before the capability check. When its mode is `none`, the new
routes still require valid capabilities, but **other existing administration
routes do not thereby become protected**.

Do not expose the entire host to untrusted agents or give them the shared global
administrator bearer. Restrict a gateway to the new invocation routes, retain a
separate administration boundary, require TLS for non-local traffic, and configure
network/request-rate/time limits. Secure forwarded-header and proxy configuration
is deployment work, not supplied by this patch. The profile is not a complete
multi-tenant authentication or authorization system.

## Routes

All routes are relative to:

```text
/api/workspaces/{workspaceId}/tools/{toolName}/invocations
```

| Method | Suffix | Behavior |
| --- | --- | --- |
| POST | (none) | Submit one explicitly identified invocation through `InvokeAsync`. |
| GET | `/{invocationId}` | Return outcome metadata through `GetInvocationAsync`. |
| GET | `/{invocationId}/approval` | Return approval state and expiry through `GetApprovalAsync`. |

The initial workspace/tool route syntax is 1–128 ASCII letters, digits, `_`, `-`
or `.`, excluding `.` and `..`. Names containing slashes or other characters are
not addressable through this ingress; existing actor identities are not migrated.
A submitted workspace must match the validated token's workspace.

Each request must supply exactly one `X-Weave-Capability` header. Its value is
base64url-encoded UTF-8 JSON containing an **already signed** `CapabilityToken`.
The token retains `tokenId`, `workspaceId`, `issuedTo`, `grants`, `issuedAt`,
`expiresAt` and `signature`; the local cancellation token is not transmitted.
The maximum header value is 16,384 characters. The web server/proxy may impose a
smaller header limit. Missing, malformed, duplicated, tampered, expired or revoked
credentials are rejected. Caller-supplied claims are never converted into newly
minted permissions. This encoding is not JWT, OAuth or encryption: protect the
whole header as a bearer credential and exclude it from proxy/HTTP tracing logs.

## Submit and interpret results

Send `Content-Type: application/json`. Encoded/compressed request bodies are not
supported. The complete request body, including JSON syntax, is limited to
1,048,576 bytes. The limit is checked while reading, even without Content-Length.

Example body, with a fresh client-retained ID for one new logical operation:

```json
{
  "invocationId": "cb5e11e2c62e4b269af74d07d6b49c98",
  "toolName": "files",
  "method": "read_file",
  "parameters": { "path": "note.txt" },
  "rawInput": null
}
```

IDs must be nonempty 32-hex GUIDs. Persist the ID and original request before
submission. Omitting it is an HTTP validation error, not an invitation for the
server to invent a new operation. Body `toolName` must match the route.

| HTTP status | Meaning at this ingress |
| --- | --- |
| 200 | POST: confirmed success or a successful recorded duplicate. GET: authorized metadata query succeeded; inspect `outcome`, which can still be `OutcomeUnknown`. |
| 202 | Waiting for approval, with no attempt dispatched. `Location` points to the approval-status route. |
| 400 | Malformed invocation, route identity or invocation ID. |
| 401 | Missing or invalid capability, or failure of the separately configured global authentication. |
| 403 | Valid credential lacks applicable authority/context. |
| 404 | Disabled ingress, absent record, or a record not visible in the caller's scope. |
| 409 | Conflicting ID/plan, terminal approval restriction, non-replayable prior call, or unknown outcome. Inspect `errorCode`, `outcome`, `approvalState` and `isReplay`. |
| 413 | Body exceeds the byte limit. |
| 415 | Unsupported media type or content encoding. |
| 422 | Other determinate non-success result, such as a blocked input. |
| 503 | Durable intent/attempt could not be admitted; this invocation was not dispatched. The existing global authentication middleware can also return 503 if unavailable. |

Unexpected infrastructure errors use the existing sanitized host error handler.
Do not infer that an effect did not happen from a generic HTTP error or transport
loss. Do not automatically create a new ID or replay an uncertain operation.

Response IDs are strings and state/outcome enums are strings. Internal branded
IDs remain unchanged. The HTTP boundary uses source-generated JSON and explicit
wire translation rather than exposing inaccessible product-generated converters.
`isReplay=true` means stored metadata was returned, not that the action ran again;
it does not restore the original response body.

Responses from the capability boundary use `Cache-Control: no-store` and
`Pragma: no-cache`. Outcome queries return IDs/status/timing without token IDs,
signatures, original payloads or a response-body cache. Approval queries require
`invocation:read`; non-owners also retain the existing backend's additional
approval authority requirements. Outcome queries retain original-subject checks.

## Approval is still a separate operator operation

Configure `Weave:Invocations:ApprovalRequiredGrants` and `ApprovalLifetime` as in
[the approval implementation record](2026-09-20-durable-approval.md). The default
requirement list remains empty. Filesystem is currently the supported approval
target-binding adapter; requiring approval on an unsupported target fails closed.

When POST returns `approval-pending`, retain its ID/request and obtain an authorized
operator decision through the existing backend. Approval alone does not dispatch.
After approval, resubmit the same ID and original request with current execution
authority. Changed input/target cannot reuse the approval. Concurrent consumption,
recording failure, expiry and unknown outcomes retain the backend's semantics.

There is deliberately no HTTP approve-by-hash button. A future review surface must
present trustworthy original inputs/target tied to the stored plan. This ingress
is not evidence that such a human-facing review workflow is complete.

## Verification and limits

`GovernedHttpEntryTests` exercises the actual host, HTTP pipeline, Orleans actor,
filesystem and on-disk SQLite journal. Coverage includes valid reads, denied writes,
credential failures, pending/approved/resubmitted calls, duplicate protection,
subject isolation, input validation and request-byte limits. Configuration tests
exercise refusal of both current and previous public development signing keys.

Supplementary cases inject actual SQLite transaction/completion failures and
check observable file effects rather than only substituted return values. A
separate `python3` process uses the standard library to call the real loopback
Kestrel port: a read-only capability reads the file but cannot write it. Python is
a required test prerequisite, not a silently skipped optional check. The client
receives its synthetic credential through stdin, not command-line arguments.

Run from the repository root after restore/build:

```bash
dotnet test --project tests/Weave.Silo.Tests/Weave.Silo.Tests.csproj --no-build -c Release
```

Exact commits, failing-first runs and final full-suite results belong in PR #103's
verification record. A targeted pass is not a full-suite pass. Source-generated
compilation is not a claim that a Native AOT publish was executed.

Durability remains one host using a preserved SQLite journal, not a cluster-wide
or arbitrary-upstream exactly-once guarantee. Preserve IDs and the journal; keep
it and credential stores outside agent-readable/writable roots. In-process
connectors remain trusted. HTTP request cancellation is attached to the local
token, but the existing Orleans cross-silo cancellation limitation is unchanged.
No administrative isolation, OAuth issuer, rate limiter, sandbox, operator UI,
resource-revision fencing or automatic recovery scheduler is introduced.
