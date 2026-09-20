# TypeScript SDK and Rust caller

The user approved C# control-plane ownership, TypeScript-first integration, and a
Rust independent caller. This increment leaves C# production behavior and the
existing product CLI unchanged. It started from PR #106 at
d500478f60bab9ac607c398a6d8ff4021c439f55; the final PR records current integration
status rather than assuming all prerequisites stayed unmerged.

## Delivered shape

`sdk/typescript` provides separate Agent (`WeaveClient`) and operator
(`WeaveOperator`, separate export) APIs. It has no runtime npm dependencies, uses
owned request snapshots and checks network result identity/types at runtime.
`clients/rust` provides a blocking caller library plus `weave-client` for invoke,
status and approval-status, not a replacement CLI or another approval engine.
Neither client holds signing keys, mints tokens, generates invocation IDs, computes
approval digests or automatically retries. Credentials are already-issued opaque
capability envelopes. Optional global Host authentication remains an additional
gate and is not an excuse to give Agents a shared administrator credential.

The [TypeScript guide](../../sdk/typescript/README.md) and
[Rust guide](../../clients/rust/README.md) contain source-build usage and actual
runtime requirements. Both packages remain private/unpublished. Python examples
and existing tests are preserved, but these clients need no Python runtime.
Rust execution also requires no .NET/Node interpreter; a Linux build does not
establish static linkage or validated Windows/macOS release packaging. The initial
TypeScript runtime target is Node 22+, not a verified browser SDK.

## Safety and semantics

Both clients enforce HTTPS outside literal loopback, refuse redirects, bound
request/response bytes, keep explicit deadlines, validate critical state/identity
fields and retain the invocation ID on unconfirmed transport errors. Rust disables
reqwest retry policy and environment proxies; TypeScript uses the trusted host
application's fetch and has no SDK retries. A replaced global transport or privileged
proxy is outside the SDK's isolation guarantee. Cancellation cannot undo a committed
decision or effect; the Rust blocking API currently has a deadline, not an async
cancellation handle.

Bare API error replies retain only the validated errorCode. Normal tool output
and review data can contain private content and still need safe handling. A reply
or process exit indicating transport success is not business success. 202 Pending,
503 NotDispatched, 409 OutcomeUnknown and 200 queries containing unknown outcomes
are not collapsed into success or retry instructions. See the
[shared scenarios](../../protocol/governed-tools/README.md).

The clients do not create an alternate backend path, secure legacy Host management
routes, prove human approval presence, provision provider connections, resolve
reviewer secrets or supply generalized tenant/resource constraints. These are
small integration surfaces over the already implemented governed HTTP path.

## Actual verification work

TypeScript tests were written first; the initial missing-module failure was setup
failure, not behavioral regression evidence. After the initial 11 passed, two
additional tests exposed runtime workspace coercion and missing NotDispatched
handling; both were corrected. Two later JavaScript-runtime tests exposed credential
coercion and Map/class parameter acceptance; those also went red before correction.

The first Rust and full .NET run, CI35490789411 at f2fe63c, built both clients and
passed all 2,678 .NET cases including the four cross-language live-host cases.
The run was NOT fully green: generated Rust formatting was not committed yet,
and C# initializer formatting reported twelve whitespace differences. No assertion
or formatter exclusion was weakened. Actual generated lockfiles and rustfmt output
were recovered from a hash-verified artifact, not manually invented.

A short-lived branch-scoped workflow imported only four exact hash-verified
artifact files as immutable Git blobs, with no checkout, artifact execution or
ref changes. It was removed after the import; no new write-enabled import workflow
remains. Final CI uses npm ci, Cargo --locked, rustfmt check, clippy and dependency
audits. Existing .NET auditing and formatting gates remain unchanged.

The shared real-socket transport tests at 671abc1 exposed one intended failure:
Rust returned an arbitrary debugCredential field from a bare error reply, unlike
TypeScript. The other fifteen cases passed, including lost-response/deadline
one-request checks. The fix filters the bare response without discarding its
status or code. Raw error details must not be reflected merely because the server
sent an otherwise valid errorCode.

The four .NET scenarios use real Kestrel/Orleans/SQLite/filesystem, alternate sender
and resumer language, and use an independent TypeScript operator credential.
They check Unicode/CRLF/null/omission, denied writes, pending/no attempt, exact
review, separate decision and execution, changed-input conflict, scoped query,
deduplication, admission rollback and uncertain completion. Network fault tests
launch actual client processes; no automatic retry hides failures.

Final exact commit, CI/audit results and artifact evidence belong in the PR.
Local TypeScript execution is distinct from Rust/.NET verification in GitHub
Actions, since the implementation container has no Rust or .NET SDK. No main merge,
package publication, deployment, production credential change or data reset is
performed by this increment.

## Contributor prerequisites

The full Weave.Silo.Tests suite now invokes the two built clients; missing client
artifacts are not skipped. Build with `npm ci --ignore-scripts --prefix
sdk/typescript`, `npm run build --prefix sdk/typescript`, and `cargo +1.98.1 build
--locked --manifest-path clients/rust/Cargo.toml` before running the full .NET suite.
This is a developer/CI requirement, not a dependency of the C# production Host.
