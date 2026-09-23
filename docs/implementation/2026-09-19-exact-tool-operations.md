# M1 increment: explicit tool operations — 2026-09-19

## Scope

Continues PR #96 at `19a04402f0dda01d522bdd1bf725e881aa53d6f1` through the same
ToolActor/connector execution path. No new framework, package, project, workflow,
approval engine, or placeholder journal. The goal is observable read/write
separation and removal of implicit agent grants, not completion of M1.

## Authority syntax

| Grant | Meaning |
| --- | --- |
| `tool:files:connect` | Connect the configured files tool; does not authorize invocation. |
| `tool:files:invoke:read_file` | Invoke this filesystem operation, not write_file. |
| `tool:files:invoke:write_file` | Explicit write operation permission. |
| `tool:files:invoke:*` | Deliberately allow every invocation on files. |
| `tool:files` | No longer authorizes connection or invocation. |

Existing explicitly broad grants such as `tool:*` remain broad under the existing
matcher. The registry narrows them to the selected tool's invocation namespace;
it does not place other tools, connection permission, or secret privileges in the
agent's resolved token. Prefer exact operations over broad presets for real use.

`ToolCapability` escapes tool/operation components separately. A literal colon,
percent sign, slash, or asterisk in a name cannot become a grant delimiter or wildcard.
Use the helper for unusual names; a displayed alias is not a complete security identity.

Each trusted connector implements a pure `NormalizeInvocation` mapping before
authorization, matching what that connector actually dispatches:

| Connector | Authorized operation selector |
| --- | --- |
| FileSystem | Lowercase operation, matching its existing dispatcher. |
| CLI | `exec`; caller-supplied labels do not make arbitrary shell commands read-only. |
| Direct HTTP | Method path without leading slashes, matching existing path handling. |
| MCP / OpenAPI / Dapr | Exact method / operation ID already used by the connector. |

The actor snapshots input and token grants before awaits, checks the operation
before secret substitution, then revalidates authority and cancellation immediately
before dispatch. Rebinding/disconnecting during those awaits prevents dispatch on
the captured connection. Existing name/workspace/signature checks, leak scanning,
secret substitution, and response behavior remain; no bypass route was added.

## Explicit agent capabilities

The existing `AgentDefinition.Tools` describes availability. Its existing
`Capabilities` field separately supplies invocation grants. For example, this
fragment gives an agent read access to an already configured `files` tool:

```json
{
  "tools": ["files"],
  "capabilities": ["tool:files:invoke:read_file"]
}
```

The host startup and individual activation paths pass both fields to the registry.
The registry copies the lists, persists them separately, and checks current rights
again after reconnect/schema awaits before minting. Availability without an
applicable invocation grant returns no resolution/token. Connection setup uses a
separate connect-only token and is not delegated to the agent.

Built-in templates explicitly declare their intended new scopes. Their former
broad tool access is now visibly expressed as invocation wildcards where retained;
that is not a claim of least-privilege presets. Custom CLI setup asks separately
which tools may receive all-operation grants, defaulting to no selection.

## Upgrade and migration

This is an intentional pre-1.0 grant/API change. Review existing manifests and
replace bare `tool:<name>` grants with the required exact operations; do not blindly
upgrade them to `:*`. Reapply reviewed configuration before expecting old workspaces
to execute tools. Old registry state lacking AgentCapabilities restores with no
invocation authority. No persisted workspace, resource, or audit data is reset.

Registry method signatures now require the capability list/map, and IToolConnector
requires the pure normalization method. Rebuild consumers/custom connectors and
use a coordinated restart, not mixed-version rolling deployment. Persisted Orleans
field IDs and existing actor keys are not renumbered. JSON old-state/default and
round-trip tests are not a claim of every production-store migration being tested.

Replacing registry capabilities prevents subsequent token minting with removed
rights; it does not automatically revoke tokens already issued. Those need explicit
token revocation or expiry. Revalidation is not an atomic transaction with a remote
effect and cannot cancel an effect already started.

## Verification and remaining boundaries

The first test-only run exposed the old tool-wide authorization and implicit minting.
One reused filesystem fixture incorrectly supplied content in Parameters rather
than RawInput; it was corrected before evaluating the final regression evidence.
The corrected test-only commit is `d59b3cd3c164f9b915c94c58ec0449e1aeb468c8`.
PR #97 records exact red/green run IDs, outcomes, and any subsequent fixes.

Tests cover actual temporary-file reads/writes, explicit connection and invocation
grants, shell selector normalization, revocation/cancellation before dispatch,
parameter snapshots, literal scope escaping, grant narrowing, old-state defaults,
and permission changes during registry resolution. A separate real Host/Orleans
integration test starts a workspace via HTTP, obtains a read-only registry token,
confirms denied writes leave the file unchanged, and verifies explicitly granted
writes. This is not an external authenticated-agent ingress demonstration.

Local checks use Python unittest and diff review. The local container has no .NET
SDK; compilation, full tests, dependency audit, and formatting run in the existing
GitHub CI. Do not substitute older run success for the exact current commit.

Tenant/Room identity, resource/account constraints, installation and schema
revisions, durable Invocation/Attempt records, durable approval, unknown-outcome
recovery, and administrative authorization remain separate work. Existing shared
connector state and failed-output redaction limitations are not solved here.
Trusted in-process connectors can bypass or misrepresent semantics; this is not a
sandbox. CLI `exec` permission still allows commands accepted by the configured
shell policy, not separate read/write semantics for every possible command.
