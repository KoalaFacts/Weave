# Governed Tools client compatibility scenarios

These are executable examples for the CURRENT HTTP invocation contract, not a
new authorization protocol or a universal plugin schema. The server alone owns
signed capabilities, operation resolution, approval validity and execution.
Clients carry opaque credentials and preserve caller-retained invocation IDs.

`requests.json` contains shared Unicode/combining-character/emoji, CRLF versus LF,
explicit null and omitted-input cases. Parameter object order is not authority:
TypeScript preserves input order and Rust uses a BTreeMap, while the existing
backend normalizes its own plan evidence. Neither client calculates approval
digests or rewrites newlines/Unicode to make an approval match.

## Verification layers

The TypeScript package's local tests validate input ownership, invalid runtime
values, expected/unknown states, aborts and actual HTTP failure behavior. Rust unit
tests validate the same fixture content and identity constraints.

`tests/client-probes/conformance.test.mjs` launches each compiled client against
controlled real sockets. It checks shared payloads, redirect credential isolation,
response loss after request receipt, deadlines with one request, response byte
limits and sanitized bare API errors. This is a network fault harness, not a mock
of the client API. Rust must be built before running it.

```bash
npm ci --ignore-scripts --prefix sdk/typescript
npm test --prefix sdk/typescript
cargo +1.98.1 build --locked --manifest-path clients/rust/Cargo.toml
cargo +1.98.1 test --locked --manifest-path clients/rust/Cargo.toml
node --test tests/client-probes/conformance.test.mjs
```

`CrossLanguageClientTests` additionally starts real Kestrel/Orleans/SQLite/filesystem
instances. It alternates TypeScript/Rust as submitting and resuming callers, and
uses an independent TypeScript operator to review and decide. It checks allowed
reads, denied writes, durable waiting without an attempt, exact review content,
original-caller resume, changed-input conflict, scoped queries and no repeated
file effect. Actual SQLite triggers verify failed admission (NotDispatched/no file
write) and failed completion recording (OutcomeUnknown/no blind replay).

Build both clients BEFORE the full .NET suite or Weave.Silo.Tests; missing binaries
are failures, not silently skipped conformance cases. Existing CI performs these
prerequisites. The C# Host itself does not acquire Node/Rust runtime dependencies.
No production data, live upstream credentials or network side effects outside
loopback fixtures are used by these cases.

## State interpretation

A returned HTTP status is not an execution outcome. 202 is waiting; 403 is denied;
409 can mean plan conflict or an uncertain recorded effect. A 200 query can contain
OutcomeUnknown. A malformed/lost response yields a CLIENT error with unconfirmed
delivery; it does not synthesize a backend NotDispatched or Failed outcome.

After any unconfirmed side-effect request, retain the original ID and reconcile.
There is no automatic retry, ID regeneration, account fallback, approval or new
execution in either client. Missing query results do not establish distributed
exactly-once or prove an upstream effect did not occur. The current backend remains
single-host/preserved-journal in scope.

Compatibility is validated against this repository revision. Unknown enum states
are rejected, not mapped to success; changing the public contract requires updating
these tests. This increment does not establish negotiation, all-provider capability
interchangeability, browser support or a published cross-platform release.
