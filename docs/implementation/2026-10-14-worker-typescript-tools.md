# Repository Node sources on native TypeScript

Repository-owned `.mjs` sources are renamed to `.ts`, including Worker tools and
Node/browser tests, the mailbox MCP example, dependency policy tools and tests,
npm Echo development scripts and tests, and the .NET cancellation fixture.
Dependencies, vendor code and generated output are outside this conversion.
Node 24 runs the source directly with native type stripping; no custom loader
or new runtime dependency is added. CI already selects Node major 24.

Ordinary tools are checked with strict TypeScript configurations:

- Worker `npm run typecheck` checks its product source, ordinary tools and the
  mailbox MCP example. The example's declaration file describes Node 24's existing
  JSON source-text APIs; it adds no polyfill or runtime behavior.
- Dependency policy `npm test` includes `npm run typecheck`; the policy CI path
  also type-checks before evaluating dependency evidence. Its new TypeScript and
  Node type packages are development dependencies.
- Echo `npm run check` checks product source and development scripts. The published
  package still contains built JavaScript and retains its existing runtime range.

Existing Node/browser tests execute as TypeScript but are not all statically
type-checked. Their assertions, denial cases and sandbox requirements remain.
New tools and tests use TypeScript. Script/import references, workflows, Python
checks and the two C# fixture paths are updated together.

## Executed migration identity

The two historical sources in `scripts/data-migrations/` are renamed without
changing any content bytes, including their original `.mjs` import/path strings:

| Source | SHA-256 of original content |
| --- | --- |
| `normalize-records.ts` | `3f62d3a2b34b51560b20ce654b6d7407823b8ae33026bb1311901c921a043ee0` |
| `legacy-records.ts` | `76e7e5ca8bdfa381c6fc270d3a4fdb375d36bbec3ad1101c323e0a0d08b5c0b0` |

The unchanged algorithm reads the two sources under their original names plus
`src/time.ts` and `src/credentials.ts`, joins their UTF-8 contents with newlines,
and hashes the result. Identity remains `normalize-mailbox-records`; the digest
remains `ecd612f0528cc03d0dee877269a5546e3531cd4d3533325fc3441938407efe62`.
It is verified before the completed-record return.

The new `scripts/run-data-migration.ts` entry stages the original file layout in
an owner-only temporary directory inside the writable Worker checkout. This
preserves package resolution through the existing `node_modules`. It copies the
historical sources without rewriting them, copies their source dependencies,
and generates the original schema-helper entry as a re-export of the typed tool.
It invokes the unchanged migration and removes the temporary directory in a
`finally` block. These generated `.mjs` files are execution artifacts, not retained
repository sources. Frozen migration artifacts are excluded from ordinary tool
type-checking because annotations would change their checksums.

The same migration still handles new, interrupted and completed conversions.
No checksum override, replacement conversion, database-history rewrite, schema
change, or production data operation is introduced. Tests pin the historical
source hashes and expected ledger checksum, check completed calls do not enter
conversion checkpoints or change ledger/contact/revision rows, and retain real
D1 interruption, exact integer, BLOB, archive and failure-closed checks.

## Behavior and verification scope

The only product-source change is the descriptive test filename in OpenAPI's
`x-schema-source` metadata. Contract fields, database schema and mailbox behavior
are unchanged. Consequently the Worker bundle can differ by that metadata text;
this change does not claim byte-identical bundles. No deployment is included.

Validation uses the package type checks and tests, the repository Python checks,
and CI's existing .NET, compatibility and protected browser gates. Exact commands,
results and reviewed commit belong in the PR evidence. A renamed source, successful
build or installed browser alone is not a passing runtime or browser test.
