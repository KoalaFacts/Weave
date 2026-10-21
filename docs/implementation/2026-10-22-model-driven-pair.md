# Model-driven pair acceptance runner

## Scope

Owner requested the missing executable two-agent demo test after the marketplace
preview was merged. This increment adds `examples/mailbox-demo-pair/`: a concrete
Codex CLI inference adapter, 1Password CLI vault adapter, bounded stdio MCP client,
two endpoint workers, supervisor, private checkpoints and evidence verifier.
It reuses the unchanged mailbox MCP and network protocol. It does not install a
remote ChatGPT connector, deploy OAuth, or create native ChatGPT subagents.

Base: `761f2cb9db612e587069be83d16cd4657577a581`. The two modified Worker config
files were fetched through GitHub and their original blob hashes checked before
editing. The working copy is a sparse reconstruction, not a full repository
clone; sandbox DNS cannot reach GitHub or the demo.

## Behavior and boundaries

The coordinator passes peer account/address IDs and turn numbers, not message
content. Each worker resolves only its configured control, uses its own MCP
process, verifies a fresh vault-backed reconnection, and reviews/accepts only the
owner-designated peer. Addresses remain private. Six model-generated messages
must be observed by the opposite endpoint and have acknowledged sender receipts.
The supervisor aggregates evidence only after the exchange.

Controls are generated and saved by 1Password before registration; no plaintext
credential files or fake vault are provided. Weave controls are not inherited by
Codex. Unknown writes retain non-secret envelopes, IDs and token digests; they
are never automatically retried. A persistent attempt marker also prevents a
blind rerun after process restart. Host prerequisites and both model probes run
before registration. These are same-user trusted processes, not hostile-code OS
isolation. Protocol decisions are deterministic scaffolding around model text.

Only the existing Worker test/typecheck entry points were extended. The new
local Worker integration fixture uses real stdio and disposable D1, but synthetic
controls and inference. No dependencies, lockfiles, deployments, releases, app
bindings, production settings or automatic public-demo CI jobs were added.

## Verification actually performed

Local Node **22.16.0** with explicit type stripping and TypeScript **5.8.3** are
not the supported Node 24.19+/pinned TypeScript 7.0.2 toolchain.

```sh
node --experimental-strip-types --test examples/mailbox-demo-pair/test/*.test.ts
NODE_OPTIONS=--experimental-strip-types npm --prefix hosts/Weave.Mailbox.Worker run test:pair
node --experimental-strip-types examples/mailbox-demo-pair/run.ts doctor
tsc --noEmit --target ES2024 --lib ESNext,DOM,DOM.Iterable,DOM.AsyncIterable --module nodenext --moduleResolution nodenext --strict --allowImportingTsExtensions --verbatimModuleSyntax --erasableSyntaxOnly --skipLibCheck --types node --typeRoots /opt/nvm/versions/node/v22.16.0/lib/node_modules/ts-node/node_modules/@types examples/mailbox-demo-pair/*.ts examples/mailbox-demo-pair/test/*.test.ts
git diff --cached --check
```

- **32 focused tests passed**, zero failures/skips. They include actual spawned
  stdio processes, timeout/exit/error handling, exact int64 parsing, mocked vault
  outcome recovery, own-context inference, paired protocol fixtures, changed
  identities, introduction mismatch, receipt/content checks and no-replay gates.
- Local strict typechecking and whitespace checks passed.
- Red tests were observed before the executable implementation and before the
  Worker test integration, final-message parsing, retained-attempt guard,
  duplicate-vault-reference guard and explicit Codex user-config exclusion.
- Actual `doctor` returned **blocked**, exit 2, `remoteWrites:false`: unsupported
  local Node version, missing Codex login/profile execution, missing secure
  vault, unreachable demo, and missing adapter source in the sparse sandbox.
  The latter is a local checkout limitation, not missing source on GitHub.

Not run locally: the added real Worker/D1 integration, full Worker/.NET/browser
suites, supported-toolchain CI, actual Codex inference, actual 1Password storage,
public registration or messages, or a ChatGPT plugin installation. Test fixtures
are not evidence of any of those successes. No accounts/items/messages were
created on external services in this implementation session.

Self-review checked repository boundaries, secret flow, unknown outcomes,
reachable failure branches, honest status messages, test assertions, and
unnecessary abstractions. It found and corrected the duplicate-reference and
ambient-Codex-config issues with red/green regressions. No independent reviewer
or native subagent execution is claimed. Supported-host live acceptance remains
a required follow-up, not a claimed result.

See the [runner guide](../../examples/mailbox-demo-pair/README.md) for exact
commands, prerequisites, costs, persistence and manual reconciliation limits.
