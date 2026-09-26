# Workspace-scoped HTTP MCP Echo installation

This increment takes the npm Echo package's loopback Streamable HTTP
server through the existing governed tool path. The process remains owned by
its deployer. The workspace manifest declares an `mcp_tools` plugin and one
MCP tool with `requires_plugin`; the plugin names the expected MCP server name,
version and one operation. This installation requires an explicit
`http://127.0.0.1:<port>/mcp` endpoint. Its HTTP limits remain at the MCP
adapter defaults.

Workspace state persists an installation ID (`workspaceId/pluginName`), URL,
expected server identity, operation, configuration digest, observed operation
schema digest and desired enabled state. Startup probes the peer before the
tool is connected, pins the schema before Agent grants are configured, and
fails if an existing installation's configuration changed. A new revision
uses a new plugin name. The active-workspace restorer probes stored enabled
installations after a Host restart; invalid or changed contracts stay
inactive. Two workspaces using the same plugin name route through distinct
installation IDs.

Only an explicit `tool:<toolName>:invoke:<operation>` Agent capability grants
invocation. Discovery for the installed connector exposes only the declared
operation. Before each call the connector makes a fresh initialize/tools-list
probe and checks the installed schema; a mismatch blocks `tools/call`. The
invocation journal, approval binding, current connector check and redaction
remain in the ToolActor path. An approval target includes the installation
configuration and pinned operation schema digest. The approval review path
resolves the installation-scoped connector.

Disabling first closes the connector's dispatch gate, then persists disabled
intent and disconnects it. Existing admitted calls drain before disposal.
Re-enabling through the plugin API requires the pinned stored contract, probes
the peer and reconnects the installation's tool. Workspace stop persists
disabled intent and leaves installation records and invocation history.

The evidence is a durable SQLite Host restart test, two-workspace routing,
schema-change denial, explicit-grant denial, disable/re-enable tests, and a
local test that ran the built npm Echo HTTP executable through the real Host.
The npm package test is opt-in to a standalone .NET suite run via
`WEAVE_ECHO_TEST_ENDPOINT` because the .NET solution does not install Node
dependencies. The npm Echo workflow builds the package and runs that test.
The default .NET test run uses an actual loopback HTTP MCP peer implemented
inside the test process.

This remains a single-host, workspace-scoped installation. It does not add
Tenant/Room authority, package installation, process isolation, credential
brokerage, a general operation catalogue, or cluster-wide dispatch fencing.
The HTTP peer can change between a probe and a later call; MCP provides no
atomic server-revision precondition for that interval. The pre-call check
reduces exposure and fails closed on observed drift; it cannot prove that a
server's implementation stayed fixed while handling a request. The Host's
current administrative authentication settings still govern the plugin API.
