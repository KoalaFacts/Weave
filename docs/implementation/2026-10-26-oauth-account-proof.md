# Optional D1 account ownership → independently consented OAuth handoff

## Status and exact boundary (2026-10-26)

This is an **unprovisioned**, testable Worker-side component. It is not an
OAuth authorization server, user-facing sign-in page, token issuer, client
registration endpoint, consent implementation, or evidence of live Agents.
There is no new table, migration, shared account, AS request or remote deployment.

An existing Weave account control is a long-lived capability. Never place it in
an AS URL, external OAuth form, MCP tool argument, access token, untrusted
ChatGPT prompt, browser redirect, logs or third-party request. The earlier
`GET /v1/accounts/me` is **local-only**, not a credential-exchange API for the AS.

## Opt-in account-proof route

`POST /v1/accounts/proof` accepts JSON `{"challenge":"<32 random bytes,
canonical unpadded base64url>"}` authenticated by the existing Weave
`X-Weave-Account-Control` header, sent **directly to Weave's own HTTPS origin**.
It reads the authenticated immutable D1 account UUID and returns only
`{"proof":"wop1.<base64url-json>.<base64url-hmac>"}`.
The HMAC-SHA256 assertion contains exact `v,iss,aud,sub,nonce,iat,exp` claims
(60s TTL), is bound to one fixed AS issuer and the exact `/mcp` resource,
and cannot be issued for a caller-selected account. Its base64url JSON account
UUID is NOT encrypted or secret. It does not grant a private MCP scope.

The route fails closed (503) unless the operator has installed all of
`OAUTH_ISSUER`, `OAUTH_RESOURCE`, `OAUTH_ACCOUNT_PROOF_KEY`.
The key must be 32 independently random bytes as 43-character base64url,
injected as a Worker **secret**, not git, config, a mailbox control, or an OAuth
bearer. The operator-approved AS separately needs secure access to that key
for proof verification. Incorrect or missing control returns 401;
malformed challenge or extra JSON members return 400; no account creation
or D1 mutation occurs. No signing key has been provisioned here.

## AS requirements before a real grant

1. Validate the OAuth client/redirect, PKCE S256, exact issuer and
   resource/audience. Generate and durably store a fresh, unpredictable
   32-byte challenge tied to **one** authorization transaction.
2. Show the account owner a trusted first-party Weave link. The owner sends
   its account control and the supplied challenge **only to Weave**, and
   returns the short-lived proof to the trusted AS, not the control itself.
3. Verify signature, exact issuer/resource/account/nonce/TTL. Match the
   expected transaction and **atomically consume the challenge once**
   before advancing consent. A valid signature by itself is replayable
   until expiry and **is not** user consent; deny missing, expired,
   mismatched, or already-consumed challenges. The AS must own and
   approve its durable state design.
4. Show the actual client, scopes and account to the human; require a
   separate explicit consent decision. Only then issue PKCE-bound,
   exact-resource tokens with `userId === props.accountId === proven UUID`,
   expiry ≤900s, and revocation. No grant for unconsented/synthetic owners.
5. Keep the resource server's fresh `AUTH_SERVER.validateToken()`
   checks and D1 owner/scope isolation on **each** private MCP request.

The AS transaction store/KV/consent/registration configuration, private
Service Binding, secrets, real browser flow, deployment and two real
Agent message/ACK/restart acceptance remain **separately blocked** until
an authorized provider exists and they are verified. Do not install
credentials or migrate D1 based only on this document.

## Evidence to retain

The portable WebCrypto HMAC tests exercise bad signature/issuer/audience,
challenge and expiry, while local Node24 Miniflare/D1 integration tests
cover two existing accounts, control isolation and no database mutation.
Node22 portable crypto tests are not equivalent to hosted Node24 Worker
verification; require the exact-head Worker/D1, .NET, protected browser,
coverage, dependency/licence and security CI before merging.
