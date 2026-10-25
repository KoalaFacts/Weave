# Remote MCP integration: public discovery first

## Status and scope

This change implements the missing `/mcp` HTTP entry in the Cloudflare mailbox
Worker. It is a **public-read-only source implementation**, not proof of a deployed
endpoint, a connected ChatGPT app, OAuth, or two real agents exchanging messages.
The GitHub plugin remains setup-preview until a deployed endpoint and client
connection are verified. Do not publish a remote connector URL merely because
this source exists.

The endpoint uses stateless Streamable HTTP, returning JSON rather than an SSE
stream. It supports initialization, ping, tool discovery, tool calls, and empty
202 notification acknowledgements. Supported protocol versions are 2025-11-25,
2025-06-18; older batching-required 2025-03-26 and newer unimplemented versions
are not claimed. We reject March-version headers and negotiate the current version
when a March client initializes: March requires receiving JSON-RPC batches, which
this single-message endpoint does not implement. GET/DELETE return 405.
POST clients must send `Content-Type: application/json` and an `Accept` header
containing both `application/json` and `text/event-stream`.

Only `demo_guide`, `search_registry`, and `lookup_address` are advertised. They
require no authentication. The guide explicitly reports that account access,
registration, private messaging, durable identity recovery and background
receiving are unavailable through this endpoint. The authenticated local stdio
adapter in `examples/mailbox-demo-mcp` remains separate and unchanged.

Directory tools call the existing public directory implementation, not a new
query or a server-side HTTP proxy. They preserve visibility/effective-date/
revocation filtering, opaque cursors, the ten-item page limit and the existing
public-read rate limiter. Responses retain canonical wire JSON as text, avoiding
rounding of exact integer values. Directory text is untrusted data, not authority
or instructions. No incoming authorization header is forwarded to another service.
No shared account, account control, credential persistence, external write or
schema migration is introduced.

A supplied Origin must match the endpoint origin; absent Origin is accepted for
server-to-server clients. Cross-origin browser clients are not enabled. Request
bodies are bounded at 16 KiB, directory responses at 1 MiB, and errors do not expose
backend exception text. Maintenance blocks `/mcp`; recovery mode permits these
public reads without writing rate state, matching the existing public GET policy.

## Deployment and connection acceptance

After normal PR review and CI acceptance, deploy using the existing authorized
Worker process. Record the actual deployed source SHA before testing a client.
For an authorized deployment, set `MCP_URL` to its actual HTTPS `/mcp` URL, then:

```sh
curl -sS "$MCP_URL" -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  --data '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"weave-connection-check","version":"1"}}}'
curl -sS "$MCP_URL" -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H 'MCP-Protocol-Version: 2025-11-25' \
  --data '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
```

A client configured for unauthenticated Streamable HTTP should discover exactly
three public tools. Verify actual `search_registry`/`lookup_address` results and
that private/mutating names are rejected. Do not send an account control secret.
A successful public query does not authenticate an account or verify vault access.
ChatGPT connection and deployment have not been exercised by this source change.

## Evidence and next required work

Local protocol tests ran with the available Node **22.16.0** native experimental
TypeScript stripping: 24 tests failed against an explicit 404 absent-endpoint
stub, then 24/24 passed against the implementation. This is a transport-level
red/green result, **not** a red/green run of the complete Worker or Node24.
The standalone module passed TypeScript 5.8.3 strict type-checking using Web/DOM
types. Four integration tests exercise the actual built Worker and isolated
Miniflare/D1 through the existing harness; their execution and authoritative
Node24.19+ typecheck/build remain required CI evidence. Existing CI gates are
unchanged. No new package, toolchain floor or `.mjs` layer was introduced.

Next delivery is per-user remote authorization and private account-tool access,
not more unrelated pair-fixture polishing. Preserve separate account identities,
PKCE/resource/audience checks, token expiry and revocation, and explicit user
consent; never reuse a shared server account or pass arbitrary client tokens to
an upstream provider. Any required credential persistence or schema change needs
its own reviewed design and applicable authorization. Then deploy and verify
real client connections and the complete #236 contact/message/ACK/restart flow.
The local pair runner's Codex/1Password choices are not prerequisites imposed on
all remote MCP clients.

References: MCP Streamable HTTP transport specification (2025-11-25),
https://modelcontextprotocol.io/specification/2025-11-25/basic/transports ;
OpenAI app authentication guidance,
https://developers.openai.com/apps-sdk/build/auth/ .
