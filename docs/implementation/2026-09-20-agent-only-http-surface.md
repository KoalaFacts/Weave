# Agent-only HTTP surface

This bounded core increment limits which application routes the existing Host
registers. It does not implement a new identity system or another executor.

## Configuration

```json
{
  "Weave": {
    "Invocations": {
      "Http": {
        "Enabled": true,
        "AgentOnly": true,
        "DecisionsEnabled": false
      }
    }
  }
}
```

AgentOnly defaults to false. With it enabled, the application API contains only:

- POST `/api/workspaces/{workspaceId}/tools/{toolName}/invocations`
- GET `/api/workspaces/{workspaceId}/tools/{toolName}/invocations/{invocationId}`
- GET `/api/workspaces/{workspaceId}/tools/{toolName}/invocations/{invocationId}/approval`

The Host retains its existing health probe mapping but does not register the old
workspace, agent, plugin, tool-management, skill, channel, user, marketplace,
template or audit endpoints. OpenAPI/Scalar and operator review/decision endpoints
are also absent. A credential with elevated grants does not bring those routes
back. They are not hidden UI links or request-prefix filters.

AgentOnly with governed HTTP disabled, or with DecisionsEnabled=true, rejects
startup. Missing/invalid signing material and the public development signing key
remain rejected by the existing governed endpoint configuration. This is a startup
selection, not a supported hot switch; restart after configuration changes.

## Preserved behavior

The three routes still use the existing capability authentication, workspace and
subject checks, exact-operation authorization, approval policy, SQLite admission,
cancellation and outcome logic. Configured global API authentication remains an
additional gate. AgentOnly does not issue broader tokens or turn connection rights
into invocation rights. Pending work stays pending and creates no execution attempt.

Default full-host composition remains available when AgentOnly is absent/false.
Its administrative endpoints still require the deployment's existing protection;
this change does not authorize exposing that complete surface to untrusted Agents.

## Provisioning and operator limits

This mode has no HTTP token issuance, connection provisioning, readable operator
review or approval decision route. Trusted startup/backend code must provision
connections and narrow credentials. A pending operation requires an authorized
operator path to the SAME logical backend/journal; the flag does not supply one.
Do not launch separate full/agent-only Hosts with independent SQLite files and
assume they share approval state. Separate concurrent Hosts against one file are
not established as a supported deployment by this change either.

For an all-HTTP deployment that needs administrator and reviewer access today,
keep the full Host behind a gateway with separate explicitly restricted Agent and
operator routes. AgentOnly is a narrower built-in exposure option for deployments
that provision/manage through trusted backend composition; it is not a replacement
for an isolated administration listener or a complete onboarding flow.

The existing runtime/modules are still composed. This is not a new lightweight
runtime, sandbox, full Tenant/Room isolation, load/rate limiter or public-host
security certification. Protect internal Orleans ports, signing keys, journals,
credentials and filesystem/network access. Require TLS outside loopback. A trusted
plugin with access to host internals is not contained by route registration.

## Verification

Tests enumerate actual registered endpoints and make real HTTP requests, rather
than relying on a documentation allowlist. They cover absence of administrative,
OpenAPI and operator routes; contradictory configuration; unchanged default mode;
real file read, denied write, pending approval with no attempt and scoped metadata
queries; and additive global/capability authentication. Full regression and sample
matrix results are recorded against the exact tested commit in the PR.

No dependency, database migration, user data reset, permission change or new SDK is
required. The feature branch was created after merging the four small Echo plugin
examples; it does not import the superseded SDK work.
