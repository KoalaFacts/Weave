# weave-agent-key (TypeScript)

A small helper for a program without a browser: it creates an Ed25519 agent key, registers the agent with a Weave mailbox relay, obtains DPoP-bound access tokens with a `private_key_jwt` assertion and calls the HTTP API. It mirrors the Python helper in [`python/weave-agent-key`](../../python/weave-agent-key/README.md) and implements the key protocol described in the [agent key record](../../docs/implementation/2026-10-31-agent-keys.md).

**Status: unpublished, private package.** `package.json` is marked `private` and nothing is published to npm. Run it from a checkout of <https://github.com/KoalaFacts/Weave> as shown below. `npx weave-agent-key` does **not** install this helper: a registry package of that name, if one exists, is not Weave's.

- Node.js 24 or later. No runtime dependencies: Ed25519 and SHA-256 come from WebCrypto, HTTP from the global `fetch`.
- The private key stays in the key file. The private key is never printed, logged or returned, and errors never echo key material, tokens or relay response bodies.
- The key file is created exclusively with mode `0600` and is never overwritten. On Linux and macOS a key file readable or writable by group or others is refused (`chmod 600` it). On Windows, file modes are not checked; protect the file with its ACL.

## Run from a checkout

```bash
git clone https://github.com/KoalaFacts/Weave.git
cd Weave/typescript/agent-key
```

Either run the TypeScript source directly (Node 24 strips the types; no install needed):

```bash
node src/cli.ts init agent.jwk
```

or build the JavaScript output first:

```bash
npm ci
npm run build
node dist/cli.js init agent.jwk
```

## Command line

```bash
node dist/cli.js init agent.jwk                                 # {"kid": ...}
node dist/cli.js register <origin> agent.jwk --name "My agent"  # {"accountId": ..., "kid": ...}
node dist/cli.js token <origin> agent.jwk <accountId>           # {"expiresAt": ...}
node dist/cli.js token <origin> agent.jwk <accountId> --print-token  # {"token": ..., "expiresAt": ...}
node dist/cli.js whoami <origin> agent.jwk <accountId>          # {"accountId", "kid", "keys": [...]}
```

- `<origin>` is the relay origin: `https://…`, or `http://` on loopback only, with no path, query or credentials.
- `init` only writes the key file; `register` creates the agent (repeating it with the same live key returns the same agent).
- `token` only obtains a token and prints its expiry. It prints the access token itself only when `--print-token` is given; treat that output as a credential.
- `whoami` obtains a token and calls `GET /v1/keys`, the agent's registered keys.
- A token request answered 429 with a `Retry-After` of at most 60 seconds is waited out once. A longer one is reported with the wait (`retry after N s`) and not retried.
- Exit code 2 is a usage error and 1 a refused or failed operation.

## Library

```ts
import { accessToken, createKey, fetchWithKey, loadKey, register } from './src/index.ts';

await createKey('agent.jwk');
const key = await loadKey('agent.jwk');
const {accountId} = await register(origin, key, {displayName: 'My agent'});
let token = await accessToken(origin, key, accountId);
const result = await fetchWithKey(origin, key, token, 'GET', '/v1/keys');
token = result.token;
```

- `accessToken` sends a fresh single-use assertion (`aud` is the token endpoint, lifetime 60 s) and a DPoP proof. A 429 whose `Retry-After` is at most 60 seconds is waited out once; a longer one throws `RateLimitedError` with `retryAfter` for the caller to decide.
- `fetchWithKey` sends `Authorization: DPoP <token>` and a proof whose `htu` is the URL without query or fragment, `htm` the uppercase method and `ath` the token hash. On a 401 it obtains one new token and repeats the request once; keep using the token it returns. Registration and key-only requests are never retried on 401.
- Requests never follow redirects, because a proof is bound to the URL it names. A refused call throws `WeaveHttpError` with `status` and `code` (the relay's `error` or problem `code`); the response body is not retained.
- A relay `code` is kept only when it is at most 64 characters of `A-Z a-z 0-9 . _ -` and space, otherwise it is `unexpected-error-code`. A success body that is not the expected JSON is `unexpected-response`.
- `loadKey` opens the file non-blocking and reads only a regular file, so a FIFO, a device or a symlink to one is refused at once.
- `HelperOptions` replaces `fetch`, the clock or the sleep for tests and embedding hosts. Its `signal` cancels every request and the Retry-After wait; without one, each request is cancelled after 30 seconds.

## Tests

```bash
npm ci
npm run typecheck
npm test
```

`test/vectors.test.ts` checks the cross-language wire vectors in `python/weave-agent-key/tests/vectors/` and verifies those proofs with the mailbox Worker's own DPoP and JWS code. The run against the real Worker is `typescript/mailbox-worker/test/agent-key-helper.test.ts`, part of the Worker suite.
