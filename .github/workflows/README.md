# GitHub Actions workflows

The repository has 13 current workflow files. Ordinary verification keeps its existing workflow and check identities while sharing build/audit implementation. Release, publish and deployment jobs retain separate permissions and configured environment protections.

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
| [deploy-weave-worker.yml](deploy-weave-worker.yml) | Manual on main, repository or production scope | Verify Worker/C# compatibility, discover existing `demo`/`db`, migrate and deploy only those existing resources; confirm business cleanup remains disabled. |

Path-scoped workflows retain their original path filters; Host acceptance also runs when its shared build/setup action changes. This does not turn every change into every optional acceptance run. See each workflow for its exact paths, timeouts and artifact retention. CI retains test/coverage evidence for 30 days; dependency audit evidence is retained for 14 days.

## Shared implementation

- [setup-dotnet](../actions/setup-dotnet/action.yml) installs `10.0.x` with `dotnet-quality: ga`, with optional NuGet caching. `global.json` controls resolution; its existing `latestMajor` policy remains. The NuGet publishing job uses its pinned setup action directly because it checks out validation code only.
- [build-standalone-host](../actions/build-standalone-host/action.yml) supplies the same locked restore and warnings-as-errors Host build to First Use and Codex Bridge. First Use continues to disable cache.
- [strict-nuget-audit](../actions/strict-nuget-audit/action.yml) is shared by CI Code Quality and Security Scan. It validates machine-readable audit results using [check_dependency_audit.py](../../scripts/check_dependency_audit.py); restore failure, malformed/missing evidence, reported warnings/errors or any reported vulnerability fail the job.

## Retired checks and compatibility

The old `vertical-slice-validation.yml` and `security-release-chain-validation.yml` branch-specific workflows are removed. Their useful package smoke and strict audit checks now run in normal CI/security scanning. Their automatic lockfile regeneration/commit jobs are retired; normal CI validates committed lockfiles without rewriting or pushing them.

The PR190 exact-base coverage collector is no longer invoked by CI. Its old check name, **Exact-base coverage (diagnostic only)**, remains temporarily so an unverified required-check setting cannot be left waiting for a missing name. It now depends on the actual **Build & Test** job and fails unless that job succeeds. It performs no baseline collection, rebuild, threshold exception or fabricated success. The standalone diagnostic script and its safety tests remain available for deliberate local use.

Required-check/ruleset configuration could not be verified because the read endpoint was denied. Ordinary workflow file identities, job names and Security Scan push/PR triggers therefore remain. Shared composite steps reduce duplicated maintenance without moving checks into nested reusable workflows or broadening path triggers to run unrelated suites. Release/publish/deployment workflows are not combined across credential or approval boundaries.

These changes reduce current workflow source files from 15 to 13. They do not delete historical Actions UI entries or run evidence. In particular, historical **Prepare PR90 Merge** came from `pr90-merge-preparation.yml`, which is not a current workflow file; it is distinct from the retired security-release-chain validation file.

## Release and deployment setup

NuGet publishing uses the existing `NUGET_API_KEY` Secret in its applicable scope and the `nuget` environment. Automatic publication is tied to one successful Create Release run. Manual republication requires the exact version and that run's ID; it does not select the latest release. Missing/expired/mismatched artifacts fail validation. There is no rebuild-from-source fallback. Packages use embedded symbols; the current release generates `.nupkg` only. `--skip-duplicate` handles an already-published version without selecting another version.

npm publication uses the configured npm trusted publisher and the `npm` environment, with OIDC only in the publish job. No npm write token is introduced. The first version still needs the existing npm trusted-publisher bootstrap process; source-package checks do not substitute for post-publication verification.

Worker deployment uses existing `CLOUDFLARE_API_TOKEN` and `CLOUDFLARE_ACCOUNT_ID` Secrets in the selected repository/production scope. No D1 ID/name, registration or host GitHub settings are required: existing D1 identity is discovered through the Worker binding, registration follows checked-in configuration (currently disabled), and new addresses use request origin. See [Worker deployment instructions](../../hosts/Weave.Mailbox.Worker/README.md#deploy-the-existing-worker-and-d1-database). Configured environment protection rules still apply. Local CI/fixture success is not evidence of a successful remote deployment.

Browser installation and focused verification commands are documented in [browser testing](../../docs/browser-testing.md). CI and release tests use the browser selected by the pinned .NET Playwright SDK; Worker verification uses its pinned Node SDK. No workflow should return to direct Chrome startup or debugging-port polling.
