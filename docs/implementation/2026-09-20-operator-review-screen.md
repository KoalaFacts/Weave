# Read-only operator review screen

## Scope

The existing Blazor Dashboard now has `/approvals/review`, reached through the
**Approval review** navigation entry. It calls the [verified review endpoint](2026-09-20-verified-approval-review.md)
and displays its checked original request, requester, target, digest and expiry.
It does not approve, reject, cancel an upstream approval, execute, issue tokens,
connect tools, or add an alternative approval model. **Clear and cancel** cancels
only this screen's local upload/request and discards its snapshot.

This is an initial operator review interface, not a production human identity
provider or a complete browser decision workflow. Decision work remains a separate
increment using the existing approval mechanism. A successful preview never grants
execution authority and never means an operator has approved it.

## Configuration and prerequisites

Configure the Dashboard, not the Silo, with an administrator-selected upstream:

```json
{
  "Weave": {
    "Review": {
      "Enabled": true,
      "BaseUrl": "https://weave-host.example/"
    }
  }
}
```

`Enabled` defaults to false. Disabled mode displays configuration guidance and
makes no review request. `BaseUrl` must be an HTTPS origin without user information,
path prefix, query or fragment. HTTP is accepted only for loopback development.
The origin cannot be supplied by the operator's form. Configure an exact intended
origin and protect the configuration: this is not a general outbound proxy.

The upstream Host must separately enable the existing governed HTTP profile,
configure an approval requirement, and already have the intended tool connected.
Read the [ingress guide](2026-09-20-governed-http-entry.md) and
[approval guide](2026-09-20-durable-approval.md). A trusted issuer must provide an
independent reviewer's capability with `invocation:read`, `approval:decide` and the
exact `tool:<name>:approve:<operation>` grant. The screen does not mint that token.
If Host-wide bearer authentication is enabled, it also requires the existing API
bearer token; that token alone is not approval authority.

Use TLS and access control for the Dashboard itself as well as the upstream Host.
The Dashboard is a trusted intermediary that receives the uploaded content and
credentials. This page does not secure its other administrative pages or add a
human login system. Keep it on an appropriately protected operator network.

## Operator flow

1. Open **Approval review** and confirm the displayed configured server.
2. Enter the original workspace and tool name. Select the retained original UTF-8
   invocation JSON file, no larger than 1 MiB. Upload before entering credentials:
   selecting another file clears previous credentials and any preview.
3. Enter the independent reviewer's capability envelope and, only when required,
   the Host's global API bearer token. Select **Verify for review**.
4. Review the server-verified requester, normalized operation, registered target,
   full parameters/body, exact plan digest and UTC expiry. Use the separate
   authorized decision mechanism only after reviewing the actual content.

The file uses the existing HTTP invocation shape:

```json
{
  "invocationId": "1a628f6d417b40a19a2ebf22725ab813",
  "toolName": "files",
  "method": "write_file",
  "parameters": { "path": "note.txt" },
  "rawInput": "The complete content originally proposed."
}
```

Use the actual retained ID/request rather than this illustrative ID. The screen
accepts these camel-case fields only; duplicate keys, extra fields, non-string
parameter values or mismatched tool names are rejected rather than silently
ignored. `rawInput` can be omitted or null. The journal cannot recover a lost
original body, and a model-generated summary is not a replacement.

The first supported preview profile remains FileSystem with unchanged effective
input. Secret-substituted input or a target the server cannot safely describe
fails closed. The page does not resolve or display hidden credential values.

## Rendering and lifecycle

All content is displayed as quoted JSON string literals with Razor HTML encoding.
Newlines, tabs, Unicode and invisible direction controls are visible as escapes;
null is distinct from an empty string. There is no HTML/Markdown interpretation,
content truncation or approve button. The complete text and parameter values wrap
on narrow screens. This initial exact representation favors unambiguous inspection
over rich text; it is not a byte-for-byte rendering of an external file encoding.

A preview is labelled **Verified snapshot**, not permanently valid approval. The
local clock updates its expiry state once per second without polling the server.
At expiry it is labelled **Review expired**. The original content stays visible
for inspection, with an explicit instruction to verify again. A state change or
revocation elsewhere cannot be detected by a local timer; the next verification,
decision and dispatch still require live server checks.

Editing identity/credentials, selecting another file, clearing the screen or
navigating away cancels the local request and discards the current snapshot. A
late response cannot restore cleared content or overwrite a newer review. Upload
and picker identities are separate so a selected file's browser stream is not
invalidated while reading it. Component disposal observes the local clock task.

## Network and credential handling

The screen uses a dedicated scoped HTTP client instead of the existing general
Dashboard API client's policies. It follows no redirects, sends no cookies or
ambient credentials, uses no system proxy, and does not bypass certificate
validation. Capability and optional bearer headers belong to each request, never
to default headers. No automatic retry or approval/dispatch request is issued.

The form clears credential fields when verification starts. Credentials are not
session object fields, URLs, files or persistent browser storage in this feature.
They necessarily exist temporarily in browser/server/.NET request memory; clearing
references is not secure memory erasure. Password managers, extensions, debugging,
reverse-proxy logging and independently configured request logging remain outside
this guarantee and must be controlled by the deployment.

The client bounds request JSON to 1 MiB and response reading to 8 MiB, including
responses without Content-Length. The larger response allowance accommodates JSON
escaping and verified metadata. Invalid/expired/mismatched responses clear the
preview. Errors use fixed messages and never echo upstream response bodies,
exception details, credentials or rejected private input. Cancelled verification
is not reported as successful review.

## Verification and limits

The test-first commit `d4026b549e302fb89f0f2a0ed9452fd87ae883b4` compiled against
the unchanged Dashboard. CI 35484191916 passed all 2,641 existing tests and failed
three new cases because the page/panel did not yet exist. No compiler failure was
counted as behavioral red evidence.

Tests render the actual compiled Razor component through HtmlRenderer, exercise
the actual Dashboard session with controlled HTTP handlers and TimeProvider, and
connect that client to the real Host/Orleans/SQLite/filesystem under both API-auth
modes. They verify encoding, expiry, no action buttons, request-local credentials,
no unsafe transport configuration, changed responses, cancellation and stale
response ordering, plus no approval/attempt/file effect during review.

This test arrangement loads the Dashboard assembly built by the existing solution
rather than adding a second web-project dependency to Silo tests. Missing compiled
Dashboard output fails explicitly. Build `Weave.slnx` before running the relevant
Silo tests. Production parsing remains source-generated and reflection-free.

The first implementation passed all 2,668 tests but failed formatter checks in two
test files. Only their whitespace was corrected; no assertion, warning, audit gate
or formatting exclusion was relaxed. Final exact-commit results belong in PR #107.
.NET verification ran in GitHub Actions; no local .NET build is claimed.

Real-browser file selection, SignalR reconnect behavior and responsive visual
acceptance have not been exercised by these renderer/client tests. Do not describe
them as browser end-to-end certification. This is author-reviewed work, not an
independent security review. There is no new dependency, project, storage schema,
background workflow, main merge, release, deployment or production-data reset.
