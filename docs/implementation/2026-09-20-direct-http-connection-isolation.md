# Direct HTTP connection isolation — 2026-09-20

## Scope

A bounded prerequisite for exact-target approval, based on merged main
`02db852d59f4e994d74a950bfa76414d9dedd87d`. The Direct HTTP adapter stored
Authorization by display tool name while treating ConnectionId as a destination
URL. Two same-named connections could overwrite/remove each other's credential;
a caller-modified or disconnected handle could still dispatch an HTTP request.

This increment fixes that concrete adapter boundary. It does not introduce
approval state, new packages/projects, a credential broker or another execution
path. ToolActor authorization, invocation recording and leak protection are unchanged.

## Behavior

Connect now returns a generated opaque connection ID and stores the original
handle together with its immutable destination/authentication configuration.
The configured credential is applied to each request, not shared client defaults.
A tool name or a shared endpoint is not a connection key.

Invoke resolves the stored connection and matches all handle fields and the
invocation's tool name before creating an HTTP request. Unknown, disconnected,
or mismatched handles return `invalid-tool-connection` without sending. A supplied
URL is not interpreted as an alternate destination. Existing method-path checks,
JSON serialization, response handling and cancellation forwarding remain.

Disconnect removes only the matching connection. Reconnecting the same tool
name creates a new ID without changing other still-live handles. An invocation
that already captured a connection before disconnect can still finish; removing
an entry is not cancellation or rollback of an in-flight external effect.
No lock or database transaction is held across HTTP dispatch.

## Compatibility

Direct HTTP ConnectionId no longer contains BaseUrl. Consumers must retain the
handle returned by ConnectAsync, not fabricate URL-based handles. Identical
value copies remain valid; changed metadata does not. There is no permissive
URL fallback. After adapter/Host restart, reconnect to obtain fresh live handles.
These ephemeral connection IDs are not durable Invocation IDs or the future
Tenant-scoped plugin installation identity. No persisted invocation is reset.

## Verification

The test-only commit `46f9ce026791a434d1dafda8beec6c1cb21e1eb2` added fifteen
cases using a real DirectHttpToolConnector and in-process HttpMessageHandler.
The handler captures actual request URI, Authorization and JSON body; synthetic
fixture values and reserved `.invalid` destinations never leave the process.
The thirteen negative cases failed against unchanged production and the two
positive controls passed. The first run's eight available TRX reports are
behavioral red evidence, not a full-solution release pass.

Existing URL/path/network tests now connect normally before invoking. Path
rejection assertions remain intact, so an unrelated invalid-handle rejection
cannot make them pass. Leading-slash cases now assert the actual URI through a
local handler instead of accepting any network failure. No test case is removed.
Final exact-commit full-suite, formatting and scan results are recorded in PR #101.

## Limits

This is connection-state isolation inside a trusted adapter, not a new
subject-authentication boundary. The existing connector contract has no token
on InvokeAsync; authorization still belongs to ToolActor. Someone who can call
trusted adapters directly with a stolen valid handle is outside that boundary.

Human approval, frozen operation/resource/installation revisions, SSRF and DNS
policy, redirects, pooled cookies, client default credentials, and other adapters
are not made safe merely by this change. Existing HttpClient/handler configuration
is unchanged. Do not infer complete account or Tenant isolation from these tests.
Review is author self-review, not an independent penetration test. No main merge,
release, deployment, production key change or data reset is included.
