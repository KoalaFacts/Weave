# GitHub Actions workflows

The repository has 14 current workflow files. Ordinary verification keeps its existing workflow and check identities while sharing build/audit implementation. Release, publish and deployment jobs retain separate permissions and configured environment protections.

## Current workflows

| Workflow | Triggers | Purpose and checks |
| --- | --- | --- |
| [ci.yml](ci.yml) | Main push, PR to main, merge queue, manual | Build & Test, nonpublishing tool-package smoke, tested-owner coverage, Code Quality, Windows CLI boundaries, exact locked dependency graphs, verified snapshots and Dependency Review. |
| [scan-security.yml](scan-security.yml) | Monday 03:00 UTC, main push, PR to main, manual | Vulnerability Audit: locked restore with low-severity/direct/transitive audit, strict JSON evidence validation and deprecation report. |
| [worker-browser.yml](worker-browser.yml) | Main push, PR to main, merge queue | Worker API, D1, MCP, C# compatibility and real Playwright browser checks with Chromium protection; retains browser evidence. |
| [codex-bridge-contract.yml](codex-bridge-contract.yml) | Relevant PR paths, manual | Real Host UUID contract across restart and explicit approval; scripted protocol verification, not live human acceptance. |
| [first-use-acceptance.yml](first-use-acceptance.yml) | Relevant main/PR paths, manual | Fresh Host public HTTP walkthrough and restart at the reviewed revision. Rejects acceptance-time modifications to tracked product/build inputs and dependency locks. |
| [dependency-policy-acceptance.yml](dependency-policy-acceptance.yml) | Policy/scripts/CI paths on main/PR, manual | Pinned license-action fixtures and actual denial of dependency snapshot submission with a read-only token. |
| [echo-plugin-examples.yml](echo-plugin-examples.yml) | Echo/core/Host/MCP/test/config paths on main/PR, manual | Python, TypeScript, Rust and Java stdio examples plus governed invocation; separate language checks and evidence. |
| [npm-plugin-echo.yml](npm-plugin-echo.yml) | npm Echo and connector paths on main/PR, manual | Exact npm contents, both transports, shipped dependency audit and real Weave authorization/HTTP installation checks. |
| [release.yml](release.yml) | Manual on main, exact version | Create Release: locked restore/build/test, validated .NET tool package and provenance, six platform archives, tag at the built commit and GitHub release. |
| [publish-nuget.yml](publish-nuget.yml) | Successful Create Release on main, or manual exact version plus release run ID | Verify immutable source/run/artifact/tag identity, download that artifact, validate packages, then publish in the `nuget` environment. |
| [publish-npm-plugin-echo.yml](publish-npm-plugin-echo.yml) | Manual on main, exact package version | Verify and pack once, preserve tarball/checksum, publish through npm OIDC in the `npm` environment, then invoke published-package verification. |
| [verify-published-npm-plugin-echo.yml](verify-published-npm-plugin-echo.yml) | Called after npm publish, or manual exact version/source SHA | Install registry package, verify signatures/provenance, and exercise Weave installation, restart, disable and schema drift without publish credentials. |
| [deploy-weave-worker.yml](deploy-weave-worker.yml) | Manual on main (repository or production scope), explicit push to `publish/demo` (repository scope), or exact verified owner comment on #236 (protected production only) | Require the selected SHA already on main and its Worker verification run on main to have passed (awaited while still running, never repeated), preserve existing `demo`/`db`, and enforce completed-only schema checks for owner comments; confirm settings and published Agent Start, `/connect` and public MCP evidence. |
| [approved-human-schema-migration.yml](approved-human-schema-migration.yml) | Manual dispatch on main only, protected production job | One-time, exact-source-pinned human D1 migration for the separately approved legacy-account upgrade; never deploys current onboarding agent-key source or activates OAuth/Access at the same time. |

Path-scoped workflows retain their original path filters; Host acceptance also runs when its shared build/setup action changes. This does not turn every change into every optional acceptance run. See each workflow for its exact paths, timeouts and artifact retention. CI retains test/coverage evidence for 30 days; dependency audit evidence is retained for 14 days.

## Shared implementation

- [setup-dotnet](../actions/setup-dotnet/action.yml) installs the `10.x` SDK channel (`dotnet-quality`, default `ga`) for the .NET 10 targets and runtime, and the `11.x` channel (`dotnet-next-quality`, default `preview` until .NET 11 GA) with optional NuGet caching. `global.json` uses a `10.0.400` baseline (required by the source generator's Roslyn reference) with `latestMajor` and `allowPrerelease`, so builds select the .NET 11 SDK for the `net11.0` targets. The setup action intentionally omits `global-json-file`, which would additionally install that baseline as an exact SDK. Locked dependency verification and security audits remain required when the selected SDK changes. The NuGet publishing job uses the same `10.x`/`ga` selection through its pinned setup action directly because it checks out validation code only.
- [build-standalone-host](../actions/build-standalone-host/action.yml) supplies the same locked restore and warnings-as-errors Host build to First Use and Codex Bridge. First Use continues to disable cache.
- [strict-nuget-audit](../actions/strict-nuget-audit/action.yml) is shared by CI Code Quality and Security Scan. It validates machine-readable audit results using [check_dependency_audit.py](../../scripts/check_dependency_audit.py); restore failure, malformed/missing evidence, reported warnings/errors or any reported vulnerability fail the job.

## Retired checks and compatibility

The old `vertical-slice-validation.yml` and `security-release-chain-validation.yml` branch-specific workflows are removed. Their useful package smoke and strict audit checks now run in normal CI/security scanning. Their automatic lockfile regeneration/commit jobs are retired; normal CI validates committed lockfiles without rewriting or pushing them.

The PR190 exact-base coverage collector is no longer invoked by CI. Its old check name, **Exact-base coverage (diagnostic only)**, remains temporarily so an unverified required-check setting cannot be left waiting for a missing name. It now depends on the actual **Build & Test** job and fails unless that job succeeds. It performs no baseline collection, rebuild, threshold exception or fabricated success. The standalone diagnostic script and its safety tests remain available for deliberate local use.

Required-check/ruleset configuration could not be verified because the read endpoint was denied. Ordinary workflow file identities, job names and Security Scan push/PR triggers therefore remain. Shared composite steps reduce duplicated maintenance without moving checks into nested reusable workflows or broadening path triggers to run unrelated suites. Release/publish/deployment workflows are not combined across credential or approval boundaries.

The earlier workflow consolidation reduced source files from 15 to 13; the separately approved D1 migration and read-only preservation inventory channels now bring the total to 15. These changes do not delete historical Actions UI entries or run evidence. In particular, historical **Prepare PR90 Merge** came from `pr90-merge-preparation.yml`, which is not a current workflow file; it is distinct from the retired security-release-chain validation file.

## Release and deployment setup

NuGet publishing uses the existing `NUGET_API_KEY` Secret in its applicable scope and the `nuget` environment. Automatic publication is tied to one successful Create Release run. Manual republication requires the exact version and that run's ID; it does not select the latest release. Missing/expired/mismatched artifacts fail validation. There is no rebuild-from-source fallback. Packages use embedded symbols; the current release generates `.nupkg` only. `--skip-duplicate` handles an already-published version without selecting another version.

npm publication uses the configured npm trusted publisher and the `npm` environment, with OIDC only in the publish job. No npm write token is introduced. The first version still needs the existing npm trusted-publisher bootstrap process; source-package checks do not substitute for post-publication verification.

Worker deployment uses existing `CLOUDFLARE_API_TOKEN` and `CLOUDFLARE_ACCOUNT_ID` Secrets. Manual dispatch on `main` selects repository or production scope; production retains its environment protections. An additional exact `/weave-deploy-demo` comment by the verified GitHub member `ldsenow` on issue #236 is supported for connected clients without `workflow_dispatch`. The workflow requires the exact owner, issue, non-PR conversation, `MEMBER` relationship and comment body, and independently rechecks the selected `main` SHA before and after the **existing protected production** approval. This path cannot select a new OAuth bootstrap or any data migration; it always uses `completed-only` and the existing Cloudflare secret. Other comments must skip all jobs. The release only succeeds after post-deploy `/connect` and MCP validation. This trigger does not give ChatGPT direct Cloudflare token access. An explicit push of an already-merged commit to `publish/demo` instead selects repository scope and automatically performs verified remote migration/deployment. Treat that branch as a release signal, not an ordinary working branch. Normal main pushes do not deploy. The selected SHA must already be on main and pass the verification job. Do not force-update the release branch or treat local checks as deployment authorization. No D1 ID/name, registration or host GitHub settings are required: existing D1 identity is discovered through the Worker binding, registration follows checked-in configuration (enabled for this public demo, with a total-account cap of 100, 10 registrations per source IP per hour, and 100 globally per hour), and new addresses use request origin. This is a demo only: data may be periodically cleared; do not store sensitive or important information. See [Worker deployment instructions](../../typescript/mailbox-worker/README.md#deploy-the-existing-worker-and-d1-database). Configured environment protection rules still apply. Local CI/fixture success is not evidence of a successful remote deployment.

**Separately approved legacy-human D1 migration:** Do not use the current-main generic `maintain` deploy or a `publish/demo` push to upgrade the existing legacy `accounts` table: newer agent-key onboarding commits removed that earlier migration path and are not approved for a combined live cutover. After its own exact-head CI/review and normal merge, manually dispatch [Approved D1 Human Account Migration](approved-human-schema-migration.yml) **from `main`**, entering the exact pinned source `150ec32e5f2415ed3c64db7e1c0d36655f8dabea` in `confirm_approved_source`. The protected `production` job then checks out only that commit, rechecks main ancestry and executes only `maintain` plus the approved human-account upgrade using the existing `CLOUDFLARE_API_TOKEN`. The pinned deployment code refuses unexpected D1 shapes, checks a D1 Time Travel bookmark before maintenance, verifies the actual public `/mcp` maintenance response before schema writes, retains the final recovery bookmark and a seven-day evidence artifact, and keeps OAuth/Access activation disabled. A refused preflight is a **failed release**, not permission to reset, restore, bypass drift guards, or ship the later agent-key schema. A completed migration still needs separate OAuth activation, independent public two-Agent messaging/ACK/restart acceptance and least-privilege revalidation.

Browser installation and focused verification commands are documented in [browser testing](../../docs/browser-testing.md). CI and release tests use the browser selected by the pinned .NET Playwright SDK; Worker verification uses its pinned Node SDK. No workflow should return to direct Chrome startup or debugging-port polling.


### Read-only live D1 preservation inventory (before any old migration)

The protected [Read-only Weave D1 Preservation Inventory](read-only-d1-inventory.yml)
workflow runs manually **from main** with the existing production environment
and the existing `CLOUDFLARE_API_TOKEN` / `CLOUDFLARE_ACCOUNT_ID`.
The same read-only workflow also accepts a narrowly scoped owner-authored
issue comment when a connected GitHub client cannot call `workflow_dispatch`:
create the **exact** comment `/weave-d1-inventory` on issue #236 using the
verified existing repository member `ldsenow`. Other commenters, issue
numbers, pull-request conversations and edits cannot start the inventory job.
Both entry paths use the **same protected `production` environment**, exact
main checkout, read-only token permissions, shared deploy concurrency lock,
and existing Cloudflare Secrets; any environment reviewer approval remains
required. This is not an unattended deployment or permission escalation.
It never uploads a Worker, changes D1, creates OAuth resources or modifies
permissions. It checks the real `demo` Worker binding, then reads only
schema metadata, aggregate row counts, ACK totals, human-linked account count,
legacy unmanaged `oauth_identity_links` presence/row count, and selected
referential-integrity aggregates. A missing legacy OAuth table is reported as
unverified history, not proof there were no links. The seven-day Actions artifact
`weave-read-only-d1-<run-id>/result.json` excludes identities, subjects,
controls, email, message contents, payloads and credentials. A reported orphan,
missing core table, or incompatible accounts structure fails the job without
repairing anything. Source tests assert SQL is restricted to reads.

**This is a snapshot, not a migration or a historical-preservation proof.**
In particular, a current six-column `accounts` table may be compatible with
newer source while having no proof that the old human-account migration kept
every prior record. No historical count baseline or D1 Time Travel restoration
is inferred from this report. The pinned legacy human-migration source accepts
older account shapes and **must not be dispatched merely because its workflow
exists**: inspect this current live report, available recovery evidence and
migration ledger first. If data history cannot be reconciled, stop and obtain
a verified recovery path; do not reset, backfill guessed identities, or deploy
old schema over current production. This read-only workflow also does not
fulfil the required two-independent-Agent public message/ACK/reconnect proof.

### Read-only Cloudflare Audit Logs provenance check (#236)

The same protected [D1 preservation inventory workflow](read-only-d1-inventory.yml)
accepts an **exact** `/weave-cloudflare-audit` issue comment on #236 only from
the existing verified `ldsenow` repository member, as an alternative to
`/weave-d1-inventory` (not an additional cloud/deployment workflow).
It reuses the protected production `CLOUDFLARE_API_TOKEN` and never writes
D1, Worker settings, Access configuration or secrets. Audit Logs v2 requires
`Account Settings Read` or `Account Settings Write` on the same account;
a Worker deployment-capable token is not automatically audit-capable.
No permission is added by this workflow.

The script first verifies the live `weave-389` subdomain, exact `demo`
Worker, and immutable `weave-demo-db` UUID; it then uses only GET requests
to query Cloudflare Audit Logs v2 for the fixed UTC window 2026-10-29
12:00–2026-11-02 02:00. It paginates with bounds, filters only that specific
D1/Worker and outputs a small, sanitized event classification in
`cloudflare-activity.json`. It **never persists** the raw account-wide
audit response, actor, email, IP, URI, token, subject, account/database ID,
cursor, or request/response body. A 401/403 or truncated/inconsistent response
fails closed and saves the limited receipt where available. Event absence,
Audit Logs product coverage gaps and POST `/query` do **not** establish data
preservation or deletion. This check cannot restore historical D1 rows, cannot
replace current row-level inventory or double-Agent acceptance, and is not
authorization to expand Cloudflare permissions or reset data.
