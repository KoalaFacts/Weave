# Two model-driven endpoints on the Weave demo

This is an **executable acceptance runner**, not a connected ChatGPT plugin and
not native ChatGPT subagent dispatch. Two separate endpoint processes own their
Weave credentials, MCP processes, state and model histories. Each uses Codex CLI
for its own message text; the deterministic runner handles registration, contact
approval, delivery and receipts. The supervisor exchanges only peer identifiers
and turn numbers until it collects the final evidence. It never forwards the
conversation text to the other endpoint.

The live path has no simulated model or relay fallback. A synthetic fixture test
passing does **not** establish that Codex, the vault or the public demo worked.
See the [implementation and verification record](../../docs/implementation/2026-10-22-model-driven-pair.md).

## Prerequisites

Use a trusted POSIX machine or WSL with Node **24.19+**, a complete Weave checkout,
network access to `https://demo.weave-389.workers.dev`, and:

- An installed, authenticated **Codex CLI** supporting `exec --ephemeral`,
  `--ignore-user-config` and JSONL events. Choose a model actually available to
  that login. Use a dedicated Codex profile with no MCP servers/plugins/hooks.
  Normal Codex login is sufficient; never paste login files or keys into chat.
- An installed, authenticated **1Password CLI (`op`)** with permission to create
  and read two Password items in a dedicated test vault. This is the first
  supported vault provider, not an assumption that every agent has a vault.
  Use the vault's exact 26-character ID. Authenticate through 1Password's normal
  supported flow, not by putting service tokens into command history.

The two endpoints may use one Codex subscription login, but they have different
Weave accounts and model contexts. A successful run uses **eight model calls**:
two readiness probes before registration, then six conversational turns. These
consume the configured account's usage. No inference runs on the relay itself.

Model processes receive a restricted environment without vault or Weave control
variables; tools/apps/hooks/search are disabled for this text-only task, and the
read-only sandbox remains enabled. This is **not OS isolation against hostile
same-user processes**. The trusted endpoint processes can access their authorized
vault. Protect the machine and its Codex/vault configuration accordingly.

## Run from the repository root

First, inspect prerequisites. This reads the public directory, CLI login status,
and vault availability; it does not register, create vault items or send messages:

```sh
node examples/mailbox-demo-pair/run.ts doctor --vault YOUR_VAULT_ID
```

Continue only when it reports `ready: true`. Pick an unused private state path
outside the repository, under an existing parent directory. Replace the model
placeholder with an available Codex model; do not use a model name from an example
as proof of access.

```sh
node examples/mailbox-demo-pair/run.ts init \
  --state "$HOME/.weave-pair-test" \
  --vault YOUR_VAULT_ID \
  --model YOUR_CODEX_MODEL \
  --allow-vault-writes

node examples/mailbox-demo-pair/run.ts run \
  --state "$HOME/.weave-pair-test" \
  --allow-demo-writes
```

`init` asks 1Password to generate and save two distinct random 64-character
controls before any registration. Disk contains only vault references and
non-secret configuration, in owner-private directories/files. The controls are
resolved in trusted processes and injected only into the corresponding MCP
process environment. They are never printed, put in argv or copied to JSON.
A process environment is not treated as a vault.

`run` starts two independent endpoint workers and proves both model runtimes
before creating either demo account. Each endpoint registers with its own saved
control, closes the MCP binding, re-reads its vault item, authenticates through a
fresh binding, reconnects and verifies the original Account ID. Changed identity
or failed authentication stops the test instead of silently replacing it.

Both addresses remain private. A introduces itself; B reads the actual
introduction from its inbox, checks the expected IDs/run, accepts and ACKs that
introduction. A and B exchange six messages about a fictional desk-lamp testing
checklist. Each reply is generated from that endpoint's actual inbox history.
A consumes the final reply. Both independently inspect sender receipts.

No public-directory strangers, publication, unblocking, account deletion,
deployment, remote-MCP provisioning or plugin installation is performed.
This does not change the empty app binding of the marketplace preview.

## Evidence and failures

The private state directory contains `result.json`, per-endpoint transcripts,
model thread IDs/token counts and write-intent checkpoints. `completed` requires
six matching sender/receiver records, one reply chain, distinct model invocation
IDs and actual acknowledged outbox receipts. Generated text saying "sent" or
"done" is not accepted as evidence. `requestedModel` records the requested CLI
model, not a claim that a provider reported its resolved model identifier.

Credentials, connection tokens, raw CLI stdout/stderr and model reasoning are
not saved in evidence. The small synthetic conversation is retained in plaintext
for inspection. The relay also receives plaintext. Do not use private, important
or sensitive content; demo data can be periodically cleared.

| Result | Meaning / next action |
| --- | --- |
| `blocked` / exit 2 | Prerequisite missing. No test account or message write was attempted. Fix the named prerequisite, not the credentials. |
| `completed` / exit 0 | This run met the runner's evidence checks; not a claim of general production readiness. |
| `not_completed` / exit 1 | Examine retained intent/evidence. Remote writes may already have committed. Do not blindly repeat. |
| `vault_setup_requires_reconciliation` | A vault create had an unknown/partial outcome. Inspect the retained item title in 1Password; do not create another item to hide it. |
| `existing_attempt_requires_reconciliation` | A prior run marker exists, even after success. The runner intentionally does not replay it. |

An interrupted run preserves its original run ID, controls, payloads and IDs.
There is no automated resume/retry command in this increment. Reconcile the
exact pending operation using the [canonical recovery guide](../../hosts/Weave.Mailbox.Worker/src/agent-start.md)
and retained metadata; do not delete checkpoints to force another attempt.
A deliberately new state directory means a deliberately new pair, with new
vault items and accounts. The runner never deletes vault items or remote accounts.

## Development checks

The standalone tests require no new npm dependencies and never access the public
demo, Codex login or a real vault:

```sh
node --test examples/mailbox-demo-pair/test/*.test.ts
```

Worker `npm test` includes these checks and
`test/live-pair.test.ts`, which runs real stdio MCP processes against disposable
local Worker/D1 with **synthetic inference and synthetic controls**. The latter
proves the protocol boundary, not live model/vault behavior. Existing Worker
typechecking includes the new TypeScript files. No dependency or lockfile changed.

The current authoring sandbox cannot run the live test. Its missing runtime,
CLI/vault and network prerequisites are recorded, not converted into a simulated
success. A supported-host live run and review remain release acceptance gates.

## Official references checked 2026-10-22

- [Codex non-interactive execution and JSONL evidence](https://developers.openai.com/codex/noninteractive/)
- [Codex CLI flags](https://developers.openai.com/codex/cli/reference/)
- [Codex configuration](https://developers.openai.com/codex/config-reference/)
- [1Password item generation and storage](https://developer.1password.com/docs/cli/reference/management-commands/item/)
- [1Password secret references in scripts](https://developer.1password.com/docs/cli/secrets-scripts/)
