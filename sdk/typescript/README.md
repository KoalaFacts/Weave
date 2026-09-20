# Weave TypeScript integration SDK

A small Node.js client for the existing governed invocation HTTP contract. The C#
control plane still owns authentication, authorization, exact-plan approval,
durable admission and execution. This SDK neither hosts an Agent nor implements
those rules. Agent and operator entry points are separate.

**Source-only, pre-release.** The package is private and has not been published to
npm. Build it from this checkout; the name in package.json is not a registry
installation promise. Node 22+ is required. There are no runtime npm dependencies;
Python and .NET are not needed by this client. Browser, Bun and Deno support have
not been verified by this increment.

## Build and test

From the repository root:

```bash
npm ci --ignore-scripts --prefix sdk/typescript
npm test --prefix sdk/typescript
```

The build emits JavaScript and declaration files under `sdk/typescript/dist`.
An application can use a local file dependency or import this built directory.
Do not commit dist or node_modules. The generated package-lock.json is committed
and CI uses npm ci, not floating dependency installation.

## Submit a retained request

The administrator must already have enabled governed HTTP, connected the tool and
issued a narrowly scoped capability. The SDK accepts its opaque base64url envelope;
it does not accept signing keys, mint tokens, connect arbitrary tools or grant
itself permission. Protect credentials outside the Agent's accessible storage.

The following is a root-level ES module example. `original-invocation.json` must
contain the retained ID and exact inputs for the logical operation. Store that ID
before the first network call; the SDK deliberately has no automatic-ID helper.

```javascript
import { readFile } from 'node:fs/promises';
import { WeaveClient, ClientError } from './sdk/typescript/dist/client.js';

const original = JSON.parse(await readFile('original-invocation.json', 'utf8'));
const client = new WeaveClient({
  baseUrl: 'https://weave.example',
  workspace: 'demo',
  capability: process.env.WEAVE_CAPABILITY,
  timeoutMs: 30_000,
});

try {
  const reply = await client.invoke(original);
  if (reply.status === 202) {
    console.log('Waiting for a separately authorized approval:', original.invocationId);
  } else if ('success' in reply.body && reply.body.success) {
    console.log('Succeeded:', original.invocationId);
  } else {
    // Keep server status and outcome separate. A 200 GET can report OutcomeUnknown.
    console.log('Not confirmed successful:', reply.status,
      'outcome' in reply.body ? reply.body.outcome : reply.body.errorCode);
  }
} catch (error) {
  if (!(error instanceof ClientError)) throw error;
  console.error(error.kind, error.delivery, error.invocationId);
  // Do not resend here or create another ID. Reconcile through a separate query.
}
```

An invocation contains `invocationId` (nonzero 32 hex characters), `toolName`,
`method`, a plain `parameters` string-to-string record and optional nullable
`rawInput`. Internal server properties, Map instances and arbitrary classes are
not request objects. Owned snapshots prevent later caller mutation from changing
the request that was submitted.

`getInvocation(toolName, id)` and `getApproval(toolName, id)` return scoped metadata.
Query permission is separate from execution permission. After approval, an explicit
resubmission must use the original ID, original inputs and current execution
credentials; do not create a new ID for resume. Neither a missing query result nor
a dropped response proves that an arbitrary upstream effect did not occur.

## Separate operator API

Import `WeaveOperator` from `./sdk/typescript/dist/operator.js` (or the local package's
`/operator` export). Construct it separately with reviewer credentials, never with
an Agent-side shared administrator identity.

- `review(original)` requests server-verified readable content.
- `decide(original, planDigest, 'approve' | 'reject')` submits an explicit decision
  against that original content and the digest actually reviewed.

The SDK does not turn a successful review into approval automatically. The
application must display complete plain-text content and obtain the intended
operator decision; it must not synthesize an approval digest. The backend checks
independent reviewer authority and current plan validity again. An approved
decision does not itself execute the tool. The decision route is separately opt-in.

## Transport and result semantics

Each call accepts `{ signal: AbortSignal }`; the default request deadline is 30s
and supported deadlines are 1..300000ms. Cancellation/deadline failure is reported
as unconfirmed because it cannot undo an effect already dispatched. There is no
SDK retry, polling loop, redirect following, cookie credential use or automatic
switch to another account. Headers and raw exception causes are not returned in
client errors. Bare API errors retain only their validated errorCode.

Requests are bounded to 1,048,576 UTF-8 bytes and streamed responses to 8,388,608
bytes. Critical response identity, flags and state enums are checked at runtime;
unknown state values fail closed. Type declarations do not prove business success.
Server-formatted duration and timestamp strings are not automatically converted.
Malformed/unexpected responses become protocol errors retaining the invocation ID.

Only HTTPS is accepted outside literal loopback HTTP. The SDK uses the application's
Node fetch implementation; this is not isolation from a replaced global dispatcher,
a privileged proxy, a compromised process or a malicious server. Do not install
retrying transport wrappers around side-effect calls or disable TLS validation.

An optional globalBearer exists for trusted deployments that additionally require
Host API authentication. It is NOT permission to give Agents the Host's shared
administrative bearer. Restrict gateway routes and separate administration; these
clients do not secure the old Host administrative surface. Returned tool output
and review content may be sensitive and must not be blindly logged or rendered.

See [shared wire scenarios](../../protocol/governed-tools/README.md) and the
[implementation record](../../docs/implementation/2026-09-20-typescript-rust-clients.md).
