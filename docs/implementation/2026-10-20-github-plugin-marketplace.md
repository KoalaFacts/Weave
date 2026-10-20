# GitHub plugin marketplace — setup preview

## Goal and selected increment

Make Weave distributable from its GitHub custom marketplace, targeting the
future web experience: install, link an individual account once, communicate.
This increment delivers the workflow package and operator binding checks only.
It does not implement or deploy a remote MCP server or user authorization.

The existing stdio adapter remains unchanged. Bundling it would require a local
runtime; exposing one process-wide control to multiple users would not provide
account isolation. A registered remote app reference is the selected web path.
No app ID is available yet, so `.app.json` is deliberately empty, not a fake ID.

## Delivered

- `.agents/plugins/marketplace.json`: one explicit-install local-source entry.
- `plugins/weave/`: compatible plugin manifest, empty app map, self-contained
  setup/messaging skills and status/safety documentation. No MCP runtime or hook.
- `scripts/marketplace/`: dependency-free TypeScript structural checker and
  known-app binder. Normal checks accept the explicit setup preview;
  `check --require-app` rejects it until a structurally valid mapping is supplied.
- Existing Worker `npm test` runs the new checks; its tools typecheck includes
  the new TypeScript files. No dependencies, lockfiles, or workflows changed.

Setup is read-only. It distinguishes installed instructions, available tools,
public connectivity and private account readiness. Registration, publication,
contact decisions and messages require actual tools and relevant user authority.
Instructions preserve private defaults, recipient consent, exact generations,
unknown-outcome reconciliation, and the demo's no-wake/no-E2EE limits.

The binder writes public metadata only, rejects unsupported/placeholder IDs,
refuses silent replacement, and uses a cooperative exclusive lock and atomic
file replacement. It does not verify the referenced service's identity or
availability. Filesystem guards are accidental-misconfiguration checks, not a
sandbox against another process with write access to the repository.

## Verification performed

Base inspected: `afcadc0922501ac822a3d1ca0e112376a890d6ac`.
The two modified Worker configuration files were reconstructed from the GitHub
connector and their original Git blob hashes were verified before editing.
The sandbox cannot resolve GitHub for a full clone or dependency installation.

Local runtime: Node 22.16.0 with explicit experimental type stripping, **not**
the supported Node 24.19+ runtime. Local compiler: TypeScript 5.8.3 with installed
Node types, **not** the repository's pinned TypeScript 7.0.2 toolchain.

Commands actually executed:

```sh
node --experimental-strip-types --test scripts/marketplace/marketplace.test.ts
node --experimental-strip-types scripts/marketplace/marketplace.ts check
node --experimental-strip-types scripts/marketplace/marketplace.ts check --require-app
tsc --noEmit --target ES2024 --module nodenext --moduleResolution nodenext --strict --allowImportingTsExtensions --verbatimModuleSyntax --erasableSyntaxOnly --skipLibCheck --types node --typeRoots /opt/nvm/versions/node/v22.16.0/lib/node_modules/ts-node/node_modules/@types scripts/marketplace/marketplace.ts scripts/marketplace/marketplace.test.ts
git diff --check
```

31 focused tests passed, zero failures/skips; structural and local type checks
passed. The strict app-required check exited 1 as expected for the empty map.
Red failures were observed before adding the package, safe concurrent-parse
error handling, and Worker verification integration. Review was self-review
against AGENTS and the applicable check-rules/adversarial-review categories;
no independent reviewer is claimed. Raw parse errors are not printed by the CLI.

Not executed: supported Node 24/pinned compiler acceptance, full Worker/.NET/
Playwright suites, actual ChatGPT import, account linking or live messaging.
Those are not implied by these focused tests. No account, production secret,
database, deployment, release, or existing installed plugin was changed.

## Remaining completion gates

1. Implement the remote MCP service with a reviewed OAuth/resource-server
   integration and per-user account-control binding outside the model. Reject
   invalid audiences, expired/revoked tokens and ungranted operations.
2. Test isolation between two separately authenticated users, reconnect without
   new identities, and consent for new account creation. Reuse existing network
   behavior rather than changing contact/inbox/outbox semantics.
3. Register the real app in the target workspace, obtain its actual public ID,
   bind it, and verify workspace availability and action permissions. A valid ID
   format alone is not proof of any of these conditions.
4. Import the reviewed GitHub revision; prove read-only setup has no side effects,
   then perform an authorized contact/request/reply flow between two real agents.
   Update preview copy/version only when those capabilities actually exist.

See [operator instructions](../../plugins/weave/README.md) for source documents
and the import/binding procedure. Main merge and deployment remain separate
operations; this record does not authorize them.
