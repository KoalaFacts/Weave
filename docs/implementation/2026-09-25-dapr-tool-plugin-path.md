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

## Authorization and failure behavior

- `requiresPlugin` must name a `dapr_tools` definition in the same manifest.
  Missing target app IDs and missing dependencies fail manifest validation.
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

This is a workspace-lifetime installation from a manifest, not a durable,
independent `PluginInstallation` with its own approval, revision, credential
references and restart/reconciliation lifecycle. A Host restart does not
automatically recreate this plugin from the prior workspace manifest. Dapr tool
connector registration is still a global Host key, so concurrent workspaces
cannot each install a different Dapr tool connector. The in-process connector
is trusted code and is not isolated from the Host.

The exercise supports extending Cordis-style reversible effects and explicit
dependency declarations to further real providers. The next justified
foundation is installation-scoped connector identity and persistent lifecycle
state, so multiple workspaces can coexist and restart safely. It does not yet
justify replacing Weave's existing authorization, invocation journal or Agent
runtime with a general Cordis context tree or typed event system.

## Verification

`DaprWorkspacePluginFlowTests` starts a real Host and a local fake sidecar,
starts a workspace from the manifest, checks denied and granted Agent
resolution, performs an authorized `ping` through ToolActor and the invocation
journal, disables and replaces the plugin, and verifies that the old tool cannot
redirect to the new connector. `ManifestParserTests` covers dependency
validation and serialization. This proves the local single-host path, not a
real Dapr deployment or multi-host behavior.
