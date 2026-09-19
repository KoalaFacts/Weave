# HTTP ingress merge review corrections

The owner authorized merging the Governed Tools increments and continuing verified
implementation. PRs #101 and #102 were merged first. New substantive comments on
#103 were reproduced before merging its HTTP entrance.

## Reproduced defects

Test-only head `e89d026bc2cb3333327fbb645e48001ba3f7e20e`, CI run
35474771914, had 2,610 passed and six intended failures across nine TRX reports.
Both sandbox configuration changes incorrectly reused approval; trailing-slash
POST returned a malformed Location; all three HTTP operations lost cancellation
before the real grain authorizer. The equivalent-sandbox reconnect control passed.
The verified artifact SHA256 is
`024d18c4670db4d90ac980d325fd30786cfb92d45290881d2b51df710c226690`.

## Corrections

The FileSystem target binding is now `filesystem-v2` and includes Sandbox alongside
root, read-only and read-limit settings. Existing v1 pending/approved plans cannot
be consumed under v2: inspect/cancel them and submit a newly reviewed operation.
There is no fallback accepting the incomplete v1 binding. Completed invocation
records still deduplicate by their retained IDs; never submit an unknown prior
outcome under a new ID. No journal or production data is reset.

Orleans-specific RPC entries carry a standard CancellationToken argument, then
restore that token to the existing ToolActor call at the receiving grain. The
three HTTP endpoints use those entries. They do not rely on serializing the
JsonIgnore capability cancellation property, and they do not merely cancel a
client-side wait. This transport adaptation stays in the existing Orleans host;
product interfaces, authorization, approval and execution rules are unchanged.
The original token-only backend calls are not claimed to transport live cancellation.

The cancellation tests wrap the real authorizer with a bounded gate inside the
real grain, cancel the HTTP request, observe the remote cancellation callback,
then release the gate and check that no file effect or invocation was admitted.
Cancellation remains cooperative: delivery can be delayed and cannot undo an
already-started effect. Unknown outcomes retain their existing semantics.

Approval Location uses validated route values and PathBase rather than appending
to the raw request path. A trailing slash does not create an empty path segment.

Reference: https://learn.microsoft.com/en-us/dotnet/orleans/grains/cancellation-tokens

Final exact-head and merged-main CI results belong in the PR verification record.
No checks, test assertions or formatting exclusions are relaxed by these fixes.
