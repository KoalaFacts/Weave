# Remote MCP: per-user delegated authorization and private mailbox operations

## Status

This increment implements the **resource-server side** of OAuth-protected
Streamable HTTP on the existing Cloudflare Worker. It reuses the existing
account, contact, inbox, outbox, send and ACK semantics; it does not provision an
authorization server, issue tokens, deploy the Worker or prove two real models
are connected. Until a trusted OAuth authorization server, account onboarding
and client connection are deployed and verified, the GitHub plugin remains
setup-preview. Do not advertise an unverified remote connector URL.

The public read-only `/mcp` route from #245 continues to work *without*
OAuth configuration. No private tool or administrative capability is available
in that state. A **partial or inconsistent** OAuth configuration fails closed.

## External OAuth authorization server contract

Use an operator-controlled OAuth 2.1 authorization server compatible with MCP
2025-11-25, RFC 9728 resource metadata and RFC 7662 token introspection.
The client/AS, not this resource server, handle interactive consent,
authorization-code flow, PKCE S256, `resource` in authorization/token requests
(RFC 8707), redirect validation and renewal.

The issuer must expose its own RFC 8414 or OpenID Connect metadata, document
PKCE S256 and support clients according to the actual client registration
policy. For OpenAI clients, provision a supported CIMD, dynamic-client-registration
or predefined-client path and verify its redirect/issuer callback contract
(RFC 9207 where advertised) with the live authorization server; a resource
server cannot implement that user consent path on its own. This Worker is **not** a substitute OAuth authorization server or
identity-provider registration system.

Set the following **Worker runtime** bindings only for a trusted provisioned AS:

| Binding | Meaning |
| --- | --- |
| `OAUTH_ISSUER` | Exact trusted HTTPS issuer, e.g. `https://login.example.invalid`. |
| `OAUTH_RESOURCE` | Exact public Worker `https://YOUR-HOST/mcp`, the audience and RFC 8707 resource. |
| `OAUTH_INTROSPECTION_URL` | Fixed HTTPS RFC 7662 endpoint on the same origin as the issuer. |
| `OAUTH_CLIENT_ID` | Confidential introspection client identity; supplied securely at deployment. |
| `OAUTH_CLIENT_SECRET` | Confidential introspection client secret, injected through Worker secrets, **never Git**. |

Do not set pretend credentials, a shared mailbox control, provider access
tokens or a secret in `wrangler.jsonc`, the plugin, query strings, model
prompts, CI logs or PRs. The checked-in Worker config leaves all five absent.

The AS must authenticate the person/agent and bind the *specific existing*
Weave account to an issued token. For this first implementation, an active
introspection response **must** contain exactly:

- `iss` equal to `OAUTH_ISSUER`;
- `aud` equal to `OAUTH_RESOURCE` (a single canonical resource string);
- `sub` equal to that authorized existing account's UUID; the resource server
  confirms the account exists in D1, and ignores any caller-supplied owner;
- `exp` (future, within 900 s), `iat` (within 900 s of `exp`, not future) and
  optional `nbf` (not future);
- `active: true` on **every** protected-tool call, and the required `scope`.

The authorization server must refuse requests for a Weave account the user
does not control; the server is the trusted issuer of this binding, not an
arbitrary ID supplied by an MCP client. The AS must support revocation so
introspection returns inactive **immediately** when access is revoked. The
Worker does not cache positive introspection, so outages fail closed (HTTP 503),
invalid/expired/revoked tokens return HTTP 401, and missing scopes return
HTTP 403. No control secret is converted into an OAuth bearer or forwarded
to any account API. The resource server sends only the supplied bearer to
its fixed introspection endpoint over HTTPS; it is never sent to a user-supplied
URI or an unrelated upstream service.

This strictly specified AS account-binding contract is a deployment
prerequisite. A generic OAuth issuer that cannot issue that binding,
resource-specific tokens or RFC 7662 revocation must **not** be connected
by weakening validation. Do not use synthetic test responses as proof of
an external provider's trustworthiness.

## Streamable HTTP and tool scope

When configured, `GET /.well-known/oauth-protected-resource/mcp` returns
RFC 9728 discovery, including `authorization_servers`, exact `resource` and
available scopes; bearer challenges include its metadata URL and the
per-tool required scope. Clients can discover tools before signing in, but
no private operation is dispatched before fresh introspection and account
binding. Authentication applies independently on every HTTP tool request,
including reconnects. Missing request IDs/notifications never execute writes.

For ChatGPT per-tool OAuth linking, 401/403 retain their HTTP status and
`WWW-Authenticate` header while also returning a JSON-RPC tool-error
`_meta["mcp/www_authenticate"]` entry containing the same bounded challenge.
Challenges include a fixed nonsecret `error_description`; AS outages (503)
never trigger a misleading linking prompt. When OAuth is enabled, public
guide/discovery explains how to authorize without claiming the anonymous call
is already authenticated. `get_my_profile` is deliberately read-only and
returns the immutable Weave account UUID bound to the verified bearer so
ChatGPT can distinguish independently connected accounts. No profile label,
account registration or identity sharing is implied.

| Tool | Required scope | Core boundary |
| --- | --- | --- |
| `get_my_profile` | `weave:addresses.read` | Stable authenticated account UUID in both `structuredContent` and text; never model-supplied identity. | 
| `list_my_addresses` | `weave:addresses.read` | Owned private/public address list, never another account's addresses. |
| `ensure_my_address` | `weave:addresses.write` | Private-by-default creation with a caller-stable UUID; exact retries do not mutate or reallocate. |
| `list_my_contact_requests` | `weave:contacts.read` | Pending/incoming/outgoing request summaries for explicit recipient acceptance. |
| `list_my_contacts`, `get_my_channel` | `weave:contacts.read` | Owner-only contacts/channel; no connection token returned. |
| `request_contact`, `decide_contact` | `weave:contacts.write` | Exact modern request IDs, address ownership, generation guard and recipient decision. |
| `list_my_inbox`, `list_my_outbox` | `weave:messages.read` | Owner-scoped inbox/outbox; opaque payload text is untrusted. |
| `send_message` | `weave:messages.send` | Accepted peer, current generation and stable payload ID; server resolves channel token in the guarded transaction. |
| `ack_message` | `weave:messages.ack` | Recipient-only ACK for sender/message ID, no automatic retry. |

All private tool arguments are bounded by the existing 16 KiB MCP request
limit. Generation values are decimal strings to avoid JavaScript int64
rounding. New address IDs are generated by the client and must be reused with
unchanged creation arguments after an unknown outcome. New addresses created
through MCP are unlisted: publishing requires a separate explicit operation.
Read responses recursively exclude control credentials and
connection tokens. Transport failures after an attempted write remain
**unknown outcomes**. A client must preserve the original request/message
ID and reconcile current inbox/outbox before retrying; do not regenerate a
payload or blindly repeat a contact request.

## Verification gates and external acceptance

Run the existing Node 24.19+ and .NET 10 test suites, including isolated
Worker/D1, protected Playwright, current-head coverage, security and
dependency/license checks. The OAuth tests inject an inert AS response for
reproducible failure/expiry/scope/revocation cases; these do **not** prove a
live AS consent flow.

Only after an authorized deployment that records the actual source SHA:
validate discovery, the live AS's PKCE S256 and `resource` handling, two
independent account bindings, denied cross-account access, private operations
and revoked-token behavior through an actual MCP client. Then complete
[#236](https://github.com/KoalaFacts/Weave/issues/236): contact acceptance,
six bidirectional messages/ACKs, process restart/recovery and deduplication,
with sanitized trace. Credentials, private payloads, tokens and account
controls must not appear in evidence.

There is no new D1 schema migration in this increment. Binding OAuth
subjects to already registered accounts is an **authorization-server
provisioning requirement**, not a claim of automatic account linking.

## Opt-in Cloudflare Workers OAuth Provider v1 Service Binding mode

The Worker now supports one additional **private RPC** token-verification mode
for Cloudflare Workers OAuth Provider v1. It is mutually exclusive with the
HTTPS RFC 7662 introspection mode above; that mode remains supported without
behavior changes. This does **not** create or deploy an authorization server.

To select the Cloudflare mode, explicitly supply only `OAUTH_ISSUER` (the
trusted AS public HTTPS issuer), `OAUTH_RESOURCE` (the exact public `/mcp`
URL), and a Cloudflare Service Binding named `AUTH_SERVER` exposing
`validateToken(resource, token)`. Do **not** set `OAUTH_INTROSPECTION_URL`,
`OAUTH_CLIENT_ID` or `OAUTH_CLIENT_SECRET` in this mode. Absent, malformed,
partial and mixed configurations fail closed. The checked-in demo config does
not declare this binding; configuring and deploying it requires the operator's
existing deployment and credential authorization.

The AS must separately authenticate the human/agent, verify **ownership of an
existing Weave account** before consent/grant, and issue resource-bound tokens.
Its validated token's `userId` and signed grant `props.accountId` must both
be that same existing account UUID. Never copy an untrusted account parameter
into either claim. The resource server also checks the matching D1 account and
required per-operation scope, exact audience/resource, and expiry before
execution. Every call validates the token again through the trusted Service
Binding (including revocation); binding failures are 503, revoked/mismatched
claims are 401, and missing scopes are 403. The AS must set a token lifetime no
longer than **900 seconds** (Cloudflare's default can be longer). The resource
server does not substitute Weave mailbox control secrets for OAuth identity.

Cloudflare v1 uses an authorization-server Worker with KV-backed OAuth state,
user consent, client registration policy and PKCE S256, plus the private
Service Binding; these AS/consent, KV, provider registration, resource and
account-grant controls must be provisioned and acceptance-tested independently.
They are **not present** merely because Weave now knows how to validate the
Service Binding result. The binding's validated token does not supply `iss`;
the trusted issuer is pinned by the operator's binding configuration and the
AS itself must enforce its token issuer, PKCE and audience during issuance.
This is not a claim that any existing login service is connected.
