# Echo HTTP connection-close lifecycle (#92)

## Established failure mechanism

The demonstration server inherits BaseHTTPRequestHandler's HTTP/1.0 behavior and
ends each handled request with close_connection=true. Its JSON replies have a
Content-Length and its notification reply is204, so the native client can finish
those responses before the server's socket close reaches it. In the tested .NET
runtime, the connection can re-enter the pool in that window without an explicit
Connection: close response header. The next POST then reaches a connection whose
handler has already finished and receives EOF before any response headers.

This explains why #92 could fail during initialization or before the streaming
call even though the SSE parser was not involved. The server process can stay
alive throughout. HTTP/1.0's default close semantics do not imply that every client
will handle the timing identically; this is the observed demo/native-client
interoperability defect, not a claim that all HTTP/1.0 servers violate a standard.

## Minimal repair and preserved behavior

McpHttpHandler.end_headers now sends Connection: close when its existing
close_connection decision is true, before flushing response headers. It does not
change the socket's established lifetime. The six-line addition is the entire
server behavior change. JSON/notification/SSE paths share that boundary.

The existing HTTP protocol version, tool schema, dispatch, response bodies,
streaming delays, stdio behavior and listening defaults are unchanged. No C#
transport, pooling configuration, retry, grant, invocation ID, persistence schema,
package, SDK or CI policy is changed. This is not global disabling of keep-alive
for Weave's providers. A peer consuming a request then closing remains a reported
failure; it does not authorize replay under the same or a fresh invocation ID.

## Causal regression and negative controls

The new Python fixture imports the actual example handler. It controls only the
schedule between completed handling and socket shutdown, isolating either a200
JSON predecessor or a204 notification predecessor. It observes client EOF or the
next queued request in a bounded close window. A queued request is drained only
to obtain orderly EOF and is never dispatched. Diagnostics contain counts,
readiness/runtime information and bounded errors, not request bodies.

The fixture itself binds port0 and announces its actual port from the bound
server. This removes a free-port handoff from the causal experiment; it does not
change the original smoke tests or assert that every unrelated port race is fixed.
The tests retain process liveness, request counts and the exact ResponseEnded
inner exception, distinguishing closure from process exit or generic failure.

Two positive cases perform initialize, initialized notification, tools/list and
ten distinct alternating JSON/SSE calls. Each checks all echoed results and
exactly13 dispatched POSTs with zero late POSTs. Two negative cases strip only
the closing header, retaining the rest of the fixed server and native client.
They still reproduce the original EOF with one late undispatched request and no
replay. A future runtime that changes HTTP/1.0 pooling must re-evaluate that
negative-control expectation explicitly, not silently delete the evidence.

The test-only source first compiled with no warnings/errors and failed in exactly
the two positive cases. Both negative controls and every pre-existing test passed.
After the header change, all four lifecycle cases and the full suite passed. The
initial red and subsequent green used .NET10.0.12; red peer diagnostics record
Python3.12.3. The original HTTP/SSE smoke tests remain present and executable.

Exact source commits, run IDs, artifact hashes, result counts, final source review
and merged-main verification belong in PR #125. No retry-until-green test loop,
assertion relaxation, skipped prerequisite or longer existing timeout was used.
Local source-byte checks and Python JSON/204/SSE/stdio controls supplement the
actual GitHub Actions .NET suite; they are not a local full .NET build.

## Limits

This closes the demonstrated connection-close mechanism behind the example's
intermittent smoke failure, not every possible ResponseEnded from an arbitrary
server or network. Historical runs did not all include wire captures, so this
record does not claim every past occurrence was individually reconstructed.
Provider crashes, TLS/proxy/network failures, unknown external effects and broader
RPC completion/recovery semantics retain their existing handling and limits.

The remaining original plan continues with #110's license/fork acceptance, then
single-Host recovery and safe provisioning. No new task or alternative framework
is introduced. No deployment, publication, credential rotation, data reset or
branch deletion is part of this change.

References: [Python HTTP server lifecycle](https://docs.python.org/3/library/http.server.html),
[.NET response completion and close handling](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/HttpConnection.cs).
