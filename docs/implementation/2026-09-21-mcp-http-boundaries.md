# MCP HTTP response and lifecycle boundaries

This increment hardens the existing outbound MCP HTTP transport. It does not add
another ingress, SDK, permission model or persistence store. HTTP response parsing
remains owned by HttpMcpTransport; its response methods are in the same-feature
partial file, with a small read-only byte guard rather than a general stream SDK.

## Changed behavior

| Boundary | Behavior |
| --- | --- |
| Redirects | The owned native handler returns 3xx to the transport; no Location is followed, even on the same origin. Configure the intended endpoint directly. |
| Cookies | The owned handler does not store Set-Cookie or send an implicit Cookie header on later calls. Browser-session authentication is not supplied by this adapter. |
| Deadline | RequestTimeoutSeconds applies to headers, response-body consumption AND waiting to enqueue frames. HttpClient.Timeout alone does not cover body reads under ResponseHeadersRead. |
| Cancellation | Caller cancellation remains OperationCanceledException. Internal timeout/disposal is an IOException category; neither is successful execution or proof of no remote effect. |
| Disposal | Signals active operations, completes the incoming queue and disposes only an owned client. Repeat disposal is harmless. |
| Response budget | Actual application-body bytes are limited before decoding or building whole lines, including comments, line endings and an initial UTF-8 BOM. At most one additional body byte is read to detect overflow. |
| Frame budget | JSON uses the smaller response/frame budget. SSE counts the UTF-8 bytes of joined data fields, including inserted newline separators and leading empty data fields. |
| Encoding/framing | Invalid UTF-8 is rejected, not silently replaced. SSE accepts one initial UTF-8 BOM and CR/LF/CRLF; an unfinished data event at EOF is an error, never a completed frame. |
| Diagnostics | Transport-visible messages retain fixed categories, status codes and configured bounds, not arbitrary peer Content-Type or HttpRequestException text. Original inner exceptions remain internal diagnostic evidence. |

Existing initial URL policy, TLS verification, proxy configuration and connection
pooling are unchanged. A validated hostname is not a DNS-rebinding defense;
protected deployments still need egress controls. Refusing redirects does not
establish a general network sandbox or protect a process with independent upstream
credentials. The borrowed HttpClient seam exists only for controlled tests; its
handler is not the production factory.

## Stream semantics and limits

The read guard limits bytes requested from the content stream before StreamReader
materializes a line. It does not claim that network/TLS/runtime buffers contain
exactly that number of bytes, nor that managed memory equals the configured cap.
A line can grow up to the configured response budget before a complete-line frame
check; memory remains bounded by that budget. Headers retain HttpClient's existing
limits. This change does not invent new numeric configuration limits or an outbound
request-size contract.

A leading empty data field contributes a newline when another data field follows.
An incomplete data event is not promoted at EOF. Encoding rejection is a deliberate
strict MCP boundary, not a claim to implement every browser EventSource behavior.
Server-push GET, automatic reconnection, session headers and general JSON-RPC shape
validation are not added here.

IdleTimeoutSeconds retains its separate per-line wait bound. The whole HTTP request
budget also bounds a busy stream or full queue, so repeated progress cannot extend
that response-consumption phase indefinitely. This is NOT a deadline for a later
JSON-RPC correlation wait after HTTP has already completed; malformed/nonmatching
RPC replies and invocation-level completion remain separate review work. Caller
cancellation must still propagate through the connection and executor.

Already delivered complete frames are not rolled back by a later stream error.
Callers must retain invocation IDs and use the existing journal/outcome semantics;
no failure authorizes a fresh-ID retry. Cancelling network I/O does not undo an
upstream effect or contain an arbitrary non-cooperative implementation.

## Verification scope

Native TCP peers exercise five redirect statuses, actual cookie isolation, JSON/SSE
body stalls, caller cancellation, disposal and a server that consumes the request
then closes without headers. Positive JSON/SSE controls each perform ten explicit
round trips and count exactly ten requests; this is not a retry-on-failure loop.

Byte-counting streams exercise fragmented multibyte input, exact and over-limit
budgets, delimiter/comment bytes, joined newlines, missing final separators,
invalid UTF-8, long unterminated lines and safe header diagnostics. A one-frame
queue pins deadline behavior when no consumer makes space. The existing adapter,
Host, authority/journal and four-language sample tests remain unchanged.

The corrected test-only revision compiled and reproduced 20 intended failures;
five new positive controls and every existing test passed. Initial test-helper
analyzer failure was a setup defect, not behavioral-red evidence. The actual
exact-head build/test counts, artifacts and review are recorded in PR #124.
No assertion, warning policy, dependency audit or formatter exclusion was weakened.

## Not a root-cause fix for #92

The existing intermittent Python HTTP/SSE ResponseEnded failure happens before
response headers. These byte, encoding and body-deadline regressions establish
separate defects. The controlled consume-and-close peer demonstrates non-retry
failure handling, not the cause of the old flake. Keep #92 open until a causal
regression reproduces that original failure and its real cause is fixed.

No production deployment, package publication, credential change, database reset,
branch deletion or main merge is implied by this implementation record.

References: [HttpCompletionOption timeout scope](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0),
[SSE parsing and framing](https://html.spec.whatwg.org/multipage/server-sent-events.html#parsing-an-event-stream).
