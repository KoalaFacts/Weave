# Dapr tool plugin path — 2026-09-25

## Delivered path

One workspace can declare a `dapr_tools` plugin and a Dapr service invocation
tool that names it with `requiresPlugin`. Starting the workspace activates the
plugin before connecting the tool. The plugin contributes only the Dapr tool
connector; it does not replace the Host event bus. An Agent must list the tool
and receive an explicit `tool:<tool-name>:invoke:<method>` capability. The
existing ToolActor then authorizes that exact operation and uses its durable
invocation journal before dispatch. Stopping the workspace disconnects tools
before disposing the plugin registration. A direct plugin disable also blocks
new calls, and a later connector of the same type cannot silently take over an
old tool handle.

Each workspace now stores a Dapr tool installation record with a stable
`<workspace-id>/<plugin-name>` identity, fixed local sidecar port, configuration
digest and desired enabled state. Tool resolution selects the connector by that
installation identity. Two workspaces can therefore use different Dapr
sidecars concurrently. Disabling an installation persists the disabled state
before removing its runtime connector; a later Host restart does not reactivate
it. Re-enabling through `POST /api/plugins` requires the installed port.
The workspace registry records the ID before workspace provisioning, so a
restart can find a workspace that reached Running even if startup was
interrupted before plugin activation.

With durable actor storage configured, Host startup reads active workspaces and
reactivates their enabled Dapr installations. Tool handles are rebuilt on
resolution. The recovery path never replays an invocation. This path was
verified with a real file-backed SQLite actor store across three Host starts:
created, recovered and called, then disabled and not recovered.

The workspace manifest is the definition and deployment input for this first
path. For example, the `manifest` inside `POST /api/workspaces` can contain:

```json
{
  "version": "1.0",
  "name": "dapr-tool-example",
  "plugins": {
    "sidecar": { "type": "dapr_tools", "config": { "port": "3500" } }
  },
  "tools": {
    "echo": {
      "type": "dapr",
      "requiresPlugin": "sidecar",
      "dapr": { "appId": "echo-service" }
    }
  },
  "agents": {
    "caller": {
      "model": "configured-model",
      "tools": ["echo"],
      "capabilities": ["tool:echo:invoke:ping"]
    }
  }
}
```

The Dapr sidecar must be available on the configured local port when the
operation is invoked. Activation validates the port but does not perform a
network health check. The example model name must be replaced with a configured
model if the Agent reasoning runtime is used. The tool can be exercised through
the governed invocation path without starting an Agent reasoning loop.

For restart recovery in a local single-Host deployment, configure
`Weave:ActorStorage:Provider=sqlite` and an appropriate
`ConnectionStrings:Sqlite` file path. The default memory actor store does not
survive restart. The SQLite extension initializes the Orleans 10.1.0 main and
persistence schema for an empty database and rejects an incomplete schema;
it does not migrate an existing schema. SQLite is for one Host, not clustered
deployment.

## Authorization and failure behavior

- `requiresPlugin` must name a `dapr_tools` definition in the same manifest.
  Missing target app IDs, missing dependencies and non-explicit or invalid
  sidecar ports fail manifest validation.
- Plugin connection still requires the Host's existing
  `plugin:invoke:<workspace-id>/<plugin-name>` grant. The workspace startup
  command mints this narrow internal token after its administrative HTTP entry.
  As elsewhere in the current Host, `Weave:Auth:Mode=none` provides no caller
  authentication. Enable Host API authentication for administrative use.
- Tool availability never grants invocation permission. The Agent's tool list
  and exact operation capability are both required to obtain a resolution;
  ToolActor rechecks the operation before dispatch.
- A plugin, tool, Agent or heartbeat activation failure during startup stops
  started heartbeats, deactivates Agents, disconnects tools, disposes activated
  plugin registrations, stops the workspace and removes it from the active
  workspace registry. If cleanup itself fails, the original failure and cleanup
  failures are surfaced together.
- Disabling the plugin blocks old Dapr connectors from issuing further HTTP
  requests. In-flight requests already sent to the sidecar may still complete;
  the invocation journal's outcome semantics continue to apply.

## Limits and Cordis decision

This is a durable, workspace-owned Dapr installation when the actor store is
durable. It is not yet a general Tenant-scoped `PluginInstallation` with
independent approval, credentials, code/package revision and multi-Host
coordination. The configuration digest covers the sidecar port and a declared
Dapr tools implementation revision. Behavior or schema changes require that
revision to advance. A previously running workspace persisted before
this change has no recoverable Dapr port; stop and start it from its manifest
to create the installation record. The in-process connector is trusted code
and is not isolated from the Host.

The exercise supports extending Cordis-style reversible effects and explicit
dependency declarations to further real providers. Installation-scoped routing
and desired-state recovery were needed here and have been added. A context tree
could later organize nested scopes and bulk disposal, but it does not provide
durable identity, grants, revision binding or recovery by itself. No current
Dapr path needs a general context tree or typed event system.

## Verification

`DaprWorkspacePluginFlowTests` starts real Hosts and local fake sidecars,
checks denied and granted Agent resolution, performs authorized calls through
ToolActor and the invocation journal, routes two workspaces to separate
sidecars, and exercises recovery and persisted disable across Host restarts.
`ManifestParserTests` covers dependency validation and serialization. This
proves the local single-Host path, not a real Dapr deployment or multi-Host
behavior.
