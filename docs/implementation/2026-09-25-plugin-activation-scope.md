# Plugin activation ownership — 2026-09-25

## Scope

This increment gives the existing Host plugin connectors an activation scope for
the runtime registrations they contribute. It draws on Cordis-style plugin
composition and disposal, within Weave's existing connector and authority path.
It does not install third-party code or introduce a new plugin package format.

The active plugin registry now reports its effective composition at
`GET /api/plugins/composition`: each active name, connector type, declared
capabilities, owned registration keys, and current activation state. It returns no plugin
configuration or credentials. The route uses the Host's existing API
authentication middleware and has the same default-auth limitation as the other
plugin routes.

## Runtime behavior

- A connector prepares its resources, then applies its registrations through one
  activation scope. If an apply step fails, the scope attempts to revert each
  attempted step in reverse order. Disconnect and Host shutdown remove the
  scope's registrations.
- Each registration is removed only if its original value is still current. A
  replacement of the same plugin name can therefore keep its new HTTP client,
  event bus, auth provider, vault provider, and Dapr tool connector registered
  while the old scope is disposed.
- The registry serializes connect and disconnect. Different plugin names cannot
  claim the same global registration key at once; a rejected claim leaves the
  active plugin in place. A replacement rejected during validation or resource
  preparation keeps the previous active status and registrations.
- The event bus proxy waits for in-flight publishes before moving subscriptions
  to another backing bus. Its gate is safe across async continuations, and its
  broker swap callback is removed when the proxy is disposed.

Connect and disconnect still require the existing `plugin:invoke:<name>` grant.
Tool invocation grants, durable admission, approvals, and outcome handling are
unchanged. A plugin's presence or registration key does not give an Agent
permission to invoke a tool.

## Boundaries

These scopes are process-local Host state, not persisted, tenant-scoped
`PluginInstallation` records. They do not supply resource binding, schema revision
pinning, external code isolation, or an installation review workflow. The
composition route reports currently active registrations; rejected attempts
remain visible through the connection result rather than as installed entries.
In-process connectors remain trusted Host code. This increment does not change
the existing default `Weave:Auth:Mode=none` behavior; operators must enable API
authentication before treating the route as an administrative diagnostic.
Registration changes are process-local and are not a transaction across arbitrary
connector callbacks or external effects.

The existing connector types and configuration shapes are preserved. The change
deliberately replaces unconditional deregistration with ownership-aware cleanup
and rejects competing global registrations. No compatibility connector or
alternate execution path was added.
