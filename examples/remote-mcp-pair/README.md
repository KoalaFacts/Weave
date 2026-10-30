# Remote MCP: two independent HTTP client processes

This is a **runnable transport acceptance client**, not a simulation of Weave itself and **not proof of two model-driven Agents**. Each child process uses its own short-lived OAuth bearer token and talks directly to the remote Streamable HTTP /mcp endpoint. The parent can acquire two separate tokens using public-client authorization-code + PKCE S256 consent in a browser, or accept separately injected existing tokens. No Weave CLI, Codex CLI, 1Password or local MCP adapter is required.

## Live prerequisites

Use Node 24+ and two existing, independently owned Weave accounts. A trusted external OAuth authorization server must already have completed consent and authorization-code + PKCE S256 linking, issued distinct resource-bound access tokens with the required private address/contact/message scopes, and support immediate revocation through introspection. The operator must explicitly approve these two accounts contacting each other. **Do not use someone else's account.**

The publicly deployed demo endpoint has independently verified anonymous MCP discovery, but the external authorization server and live per-account ChatGPT OAuth linking are **not provisioned by this repository or this script**. If the endpoint has only public tools, the script stops before any private write.

Choose **one** sign-in method. For the exact public Weave demo resource,
if the approved authorization server advertises **Client ID Metadata Documents
(CIMD)**, the runner automatically uses
`https://demo.weave-389.workers.dev/oauth-client-metadata.json` as its public client ID. This fixed HTTPS document
advertises RFC 8252 loopback redirects, including random local ports, and
requires a separate PKCE S256 login and explicit per-account consent for each
client. This avoids enabling RFC 7591 dynamic registration on the first-party
Cloudflare OAuth service. It is **public client metadata, not a credential**:
any local application could present that same client ID, so verify the local
redirect warning and consent details. You may override it with an independently
approved `WEAVE_OAUTH_CLIENT_ID`. Other MCP resources keep their supplied ID
or existing opt-in DCR flow. Neither route provides an anonymous write grant. For an already provisioned, trusted OAuth AS, either supply a pre-registered nonsecret public `WEAVE_OAUTH_CLIENT_ID`, or omit it when the issuer advertises an approved RFC 7591 `registration_endpoint`. Dynamic registration requests a public client for the exact ephemeral `http://127.0.0.1:{port}/callback` redirect with authorization-code + PKCE S256 and RFC 8707 resource indicators. It rejects client secrets, invalid client IDs, mismatched redirect URI responses, unauthorized origins, redirects and non-JSON/unbounded responses. DCR registers only a client; it neither logs in nor grants Weave account access. The script verifies protected-resource and authorization-server metadata, opens two **separate** browser-consent URLs (one at a time), checks a random state for each loopback callback, and exchanges each code with a fresh PKCE verifier. Access tokens remain in process memory, scoped to the exact `/mcp` resource, and are not written to evidence or journal. The issuer must publish valid RFC 8414 or OpenID Connect issuer metadata. Weave tries the resource-specific RFC 9728 well-known location first, then the root location only when the specific one returns 404/410. For a resource advertising more than one OAuth issuer, explicitly pin the chosen issuer with `WEAVE_OAUTH_EXPECTED_ISSUER=https://YOUR-TRUSTED-ISSUER` before attempting consent; Weave never guesses the first advertised issuer. The pinned issuer must exactly match the protected resource's list and the discovered issuer metadata. By default, its authorization
and token endpoints must share its origin. If an explicitly trusted issuer
publishes endpoint URLs on **other HTTPS origins**, pre-approve the exact
scheme/host/port origins (up to eight) without paths, queries, credentials or
fragments using the optional `WEAVE_OAUTH_TRUSTED_ENDPOINT_ORIGINS` variable,
for example `https://login.example,https://tokens.example`. The client still
checks exact issuer metadata, PKCE, state, resource, 900-second expiry and
no-redirect token exchange. Never use this option to trust arbitrary metadata
endpoints; HTTP exceptions apply only to explicit local loopback tests.

Alternatively, have an approved secret-injection mechanism supply **both** short-lived bearer tokens to the parent process without printing or storing their values:

    WEAVE_AGENT_A_ACCESS_TOKEN  (OAuth bearer for account A)
    WEAVE_AGENT_B_ACCESS_TOKEN  (different OAuth bearer for account B)
    # OR: WEAVE_OAUTH_CLIENT_ID  (optional pre-registered public ID; omission uses advertised RFC 7591 DCR)
    # WEAVE_OAUTH_EXPECTED_ISSUER=https://YOUR-TRUSTED-ISSUER  (required for multi-issuer resources)
    WEAVE_MCP_URL=https://demo.weave-389.workers.dev/mcp
    WEAVE_EVIDENCE_PATH=/secure/private/location/weave-live-evidence.json
    WEAVE_LIVE_ACCEPTANCE=1

After the variables have been securely injected into the process environment, execute from the repository root:

    node examples/remote-mcp-pair/run.ts

The evidence destination **and its `.journal` sibling** must not already exist. After read-only account/contact preflight, the script reserves both files exclusively (mode 0600) **before any private write**. Keep both private. Existing accepted account pairs are refused before creating addresses; use two previously unconnected authorized accounts. A non-demo HTTPS URL also requires an explicit WEAVE_ALLOW_CUSTOM_ORIGIN=1 trust decision. Non-HTTPS remote URLs, embedded URL credentials and redirects are rejected. Plain HTTP is allowed only on loopback for tests.

**This command performs real writes when authorized.** It creates one private/unlisted address for each participating account, requests and accepts contact between those accounts, sends six synthetic messages in alternating directions, and ACKs them. Both child processes are stopped and relaunched while two messages remain pending; the receivers then read and ACK those messages. It verifies final receipts and that a duplicate send with the original ID returns its retained acknowledgment. No unrelated contacts are invited; no other inbox entries are ACKed.

Access tokens are never passed as command-line arguments, written to the evidence file, forwarded to the other child, or included in tool-error messages. The parent deliberately restricts child environments. No automatic retry is performed when a write outcome is uncertain. Every private write is fsync-journalled with its exact operation ID, caller role and synthetic arguments before dispatch; confirmed or unknown status follows. A lost response may leave a zero-length incomplete evidence file plus `.journal`. Reconcile its retained IDs against the actual owner-scoped inbox/outbox/contact history; **never blindly rerun the whole script**, reuse a message ID with changed arguments, or delete an unresolved journal. These files do not contain bearer tokens but do contain synthetic message bytes and account identifiers. The operator must provision and authorize the external AS. The native client can be supplied or dynamically registered only when issuer metadata advertises a trusted RFC 7591 endpoint. Interactive PKCE performs only per-account consent and token exchange; token renewal, durable token storage, and the authorization server are outside this tool. If either consent fails or both accounts resolve to the same identity, no mailbox writes are performed.

## Evidence and boundaries

The result JSON contains only the endpoint, timestamps, truncated account-identifier hashes, nonsecret message IDs, directions, acknowledgment indicators and process-restart/replay indicators. It explicitly marks notModelAgentProof=true. It contains no bearer, original message text, private address, channel token or control secret. An interrupted run can have committed partial writes; its `.journal` retains exact write intents/outcomes, while the reserved result path stays incomplete rather than reporting success. Reconcile retained IDs before retrying.

The automated regression in the Worker test suite uses a **synthetic local HTTP server**, two real Node child processes and synthetic tokens. It verifies the scripted transport protocol, isolation, two pending messages over restarts, six ACKs, retained send and credential omission; it does **not** verify a live OAuth issuer, Cloudflare D1 persistence or model inference. Existing Worker/D1 tests cover separate mailboxes and private operation semantics. Run this regression as part of the repository's unchanged Worker test/typecheck gates.

To close issue #236, execute this script against an approved public deployment with real OAuth bindings, then additionally perform the same interaction with **two actual independent model Agents** using their own authorized MCP connections and recoverable credentials. Do not label a scripted run as that final acceptance.
