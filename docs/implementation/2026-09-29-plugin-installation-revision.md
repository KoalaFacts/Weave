# Plugin installation revision envelope

Dapr and MCP tool installation records now share a `PluginInstallation` base
contract for stable installation identity, desired lifecycle state, definition
revision, requested permissions, granted permissions, and credential references.
The two provider records still own their own connection configuration and
contract digests. They remain persisted in the workspace actor; this change
does not introduce a second installation database or copy an installation to
another workspace.

New records bind `definitionRevision` to the built-in connector's declared
implementation revision. Activation migrates an older record with no revision
only when its existing configuration digest matches that implementation.
A changed or unknown revision blocks restoration and re-enabling. The
inventory exposes the stored revision. An upgrade still uses a new plugin name
and installation ID; no existing approval or connection is redirected to a
changed implementation.

Current Dapr and MCP tool plugins request no authority to call other Weave
services and receive no plugin-level grants or credential references. Their
records therefore persist separate empty requested and granted lists and an
empty credential-reference list. A record carrying unsupported permission or
credential state cannot be restored or enabled. Agent invocation still requires
its separate exact `tool:<name>:invoke:<operation>` capability and the
installation-scoped connector. The `plugin:*:install` management capability
authorizes creation; it is not a runtime grant to the plugin.

This is the versioned shared installation envelope for two existing workspace
plugins. A general Tenant-scoped installation store, an operator grant workflow
for plugin-requested service permissions, credential brokerage, and in-place
upgrade transactions remain separate work. The existing workspace persistence
provider still determines whether records survive a Host restart.
