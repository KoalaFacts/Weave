# OAuth provider-agnostic interoperability experiment

User direction (2026-10-26): Weave owns protocol and business authorization,
not an OAuth management provider. Cloudflare Access Managed OAuth is one
candidate, not the core contract. This is an isolated experiment: production
handlers, credentials, deployment settings, schemas and existing PR #252 are
unchanged. No new authorization server is deployed or required by this spike.

## Executable evidence

```sh
node --test experiments/oauth-provider-agnostic/*.test.ts
node experiments/oauth-provider-agnostic/probe.ts https://YOUR-MCP-HOST/mcp
# Explicit issuer trust is required before the probe fetches issuer metadata:
node experiments/oauth-provider-agnostic/probe.ts https://YOUR-MCP-HOST/mcp https://YOUR-ISSUER
```

Use Node 24.19+. The existing OAuth Worker workflow also runs the experiment
and probes the existing demo anonymously. There is no client registration,
cloud write, credential access or token issuance in that public probe. The
probe bounds response bytes/time, refuses redirects and credentials, reports
only allowlisted diagnostics, and does not trust an advertised issuer without
an exact operator-supplied issuer. It implements well-known resource metadata
and RFC 8414/OIDC discovery; it is NOT a full OAuth SDK, a WWW-Authenticate
parser, or evidence of consent/token-exchange success. `authenticated` is
always false in a discovery report. HTTP errors are not successful discovery.

The compatibility tests import the **unchanged repository client and resource
validator**; they do not reimplement those production paths. Two independent
synthetic issuer origins run on actual loopback HTTP servers. Each checks the
PKCE verifier, exact redirect, client ID, resource, authorization code and
scope, then exercises RFC 7662 and immediate revocation. A simulated browser
callback is not a real user's consent. Local tests use copied production files
whose Git blob hashes are verified; CI imports the real checkout directly.

| Experiment | Assertion |
| --- | --- |
| Two synthetic issuers | Same client and resource code work using different metadata/configuration; token contents are opaque to the client. |
| Access-shaped `oauth:` token | The client retains it unchanged, but the current RFC 7662 ingress rejects it. This is a characterization, not an attempt to bypass Access or broaden bearer grammar. |
| Forged Access header | A bare `Cf-Access-Jwt-Assertion` never grants access. |
| External non-UUID subject | Current resource validation rejects it before account lookup. No guessed/email-based account linking. |
| 3600-second lifetime | Current client rejects it under the existing 900-second policy; the spike does not relax policy. |
| Discovery variants | Resource-path/root metadata, RFC 8414/OIDC tenant locations and explicitly selected issuer work; malformed, oversized, redirected, untrusted and unsafe responses fail closed. |

A passing characterization test can document an **unsupported integration**.
It does not mean Cloudflare Managed OAuth or any commercial provider has been
successfully connected. These tests use no real vendor credentials or agents.

## Findings and proposed boundary

1. Keep OAuth authorization-code/PKCE, metadata, resource/audience, expiry,
   revocation and scope rules in protocol-facing components. RFC 7662 and a
   separately validated JWT access-token profile are protocol choices; provider
   branding and token-shape guessing must not select trust implicitly.
2. Resolve a validated `(issuer, subject)` to authorized Weave account(s)
   separately. `sub` is not generally a Weave UUID; switching issuers must not
   link identities by a bare subject or email. Account linking/consent remains
   an explicit business authorization operation, not something a metadata
   document or request argument can grant.
3. The existing Cloudflare Service Binding path is provider-specific. Keep it
   outside the provider-neutral core if retained. A core request contract must
   not require a Workers binding, Cloudflare SDK, tenant cookie or header.
4. Cloudflare **Access Managed OAuth** is different from the Workers OAuth
   Provider library already used by the optional AS example. Official Access
   docs describe an opaque token resolved at the edge and a signed
   `Cf-Access-Jwt-Assertion` forwarded to the origin. This is not a documented
   interchangeable RFC 7662 endpoint. A deployment adapter would have to
   validate signature/JWKS, exact issuer and Access application audience,
   expiry and origin trust before explicit Weave identity/permission mapping.
   Access application admission must not be turned into blanket Weave scopes.
   Do not enable Access over an existing independent OAuth flow indiscriminately:
   it replaces the application's 401 challenge behavior.
5. The 900-second lifetime is current Weave policy, not a universal OAuth
   protocol requirement. Negotiate/configure compatible lifetimes explicitly;
   never silently remove the limit to make a vendor test pass.

## Opt-in RFC 7662 external subject resolution (not deployed)

The resource's RFC 7662 verifier now accepts a server-composed
`OAuthSubjectResolver`. After fresh issuer, audience, scope, active-state and
expiry checks, only the trusted server can resolve the exact
`{issuer, resource, subject}` triple to a Weave account UUID. It must be
backed by an explicit owner-consented, persisted binding with revocation.
Unknown/invalid links fail 401; binding-store outages fail 503. The existing
D1 account check and both token-expiry checks remain mandatory. Two issuers
may reuse the same opaque subject without sharing an account. No implicit
email/UUID fallback applies once a resolver is configured.

This is a **tested protocol extension point**, not active third-party
login. Production code does not yet supply an external resolver, link-state
schema or enrollment UI; existing accepted Weave-native UUID issuer handling
is unchanged. Schema/migration, explicit linking consent and any real vendor
deployment still need independent authorization and end-to-end testing.
Cloudflare Access signed identity assertions are *not* RFC 7662 responses.

## RFC 8414 explicitly approved cross-origin endpoint interoperability

An exact trusted issuer can advertise HTTPS authorization and token endpoints on
different origins. The existing Node MCP client now accepts the optional
`trustedEndpointOrigins` list of **operator-selected exact origins** for
this topology. Same-origin remains the default. It refuses unknown origins,
userinfo, paths/query fragments in the allowlist, plaintext remote HTTP,
and redirects. The issuer metadata is still fetched from and compared with
the previously selected trusted issuer; PKCE S256, resource, state, token
bounds and short expiration policy are unchanged. HTTP loopback support is
**test-only**. The real Node 24 loopback test uses separate issuer/endpoint
process servers, a browser callback, authorization code and PKCE token
exchange, with a negative control showing denial without explicit approval.
This does not add Cloudflare/Auth0 SDKs or establish a live provider login.

## Real Managed OAuth acceptance still required

No accessible configured Access test application or Access management connector
was available to this session. An existing isolated application URL, approved
Access application configuration and interactive user login are needed to test
actual discovery, registration/client policy, PKCE, private-tool scopes,
issuer/subject mapping and revocation. Do not send credentials in chat. A
public demo discovery probe cannot substitute for that live test. Do not
advertise the GitHub plugin as connected on the strength of these results.

Before any production adapter change: keep #252's pending client corrections,
write failing behavioral tests for the chosen protocol contract and preserve
existing security/full-suite gates. No production refactor or merge is claimed
by this experimental branch.

## Primary sources checked 2026-10-26

- MCP 2025-11-25 authorization, including RFC 9728 and RFC 8414/OIDC discovery:
  https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization
- RFC 7662 token introspection:
  https://www.rfc-editor.org/rfc/rfc7662
- Cloudflare Access Managed OAuth, opaque tokens, forwarded assertions and
  application configuration (not the Workers OAuth Provider library):
  https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/managed-oauth/
- Access JWT validation:
  https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/authorization-cookie/validating-json/

## RFC 7591 public-client registration compatibility

The runnable two-process Node client may omit `WEAVE_OAUTH_CLIENT_ID` only
when the trusted issuer advertises a `registration_endpoint`. It then
registers each consent session independently as a **public** code/PKCE
client, restricted to its ephemeral loopback redirect URI. Registration
and token calls use HTTPS outside loopback tests, bounded JSON, no redirects,
no credentials, and exact operator-approved issuer/endpoint origins. Returned
client secrets or untrusted redirect registrations are rejected before
browser consent. Pre-registered client IDs are still supported unchanged.
This is RFC 7591 interoperability, not Cloudflare provisioning, consent,
external-subject linking, or live demo acceptance.

### 401 deployment diagnosis (anonymous, read-only)

The public `probe.ts` checks the OAuth metadata URL. When it receives a
401 without usable metadata it compares three **fixed same-origin GET** routes:
root resource metadata, the `/mcp` resource GET, and the public homepage.
It emits only status codes and the `WWW-Authenticate` *scheme class*, never
its realm, URL, cookies, bearer contents, authorization data or response body.
A 401 on only `/.well-known/` suggests a route-specific gate; a 401 across
all routes suggests broader access policy, but neither result proves which
Cloudflare policy is responsible. The original 401 remains a **failed** live
acceptance gate; the diagnostics never claim authentication or enable writes.
