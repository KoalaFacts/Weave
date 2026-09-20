# Dependency review evidence gate — partial remediation of #110

## The observed defect

The existing pinned Dependency Review action can print `No snapshots were found
for the head SHA` and still succeed. The test-only baseline in PR #112, commit
`b41999b4852d0b480f6a03f32553e9059453192d`, reproduced this in CI 35496678029:
Dependency Review succeeded despite the missing-head warning. The new repository
contract test failed because there was no prerequisite evidence gate; its unchanged
severity/license/read-permission positive control passed. Of 46 Python checks,
45 passed and the one intended check failed. No .NET test run is claimed for that
baseline because the earlier repository check stopped the build job.

This is different from a legitimate empty dependency delta. A PR with no dependency
changes can legitimately return zero changes; that is not a full dependency audit,
and missing graph evidence must never be used to infer that everything was checked.

## What this increment changes

A read-only standard-library preflight calls GitHub's dependency comparison API
for the exact PR base/head SHAs before the policy action. Missing snapshot warnings,
invalid responses, API/network failures, oversize responses or pagination limits
fail the job. The downstream policy action does not run after failure. No
continue-on-error, warn-only fallback, removed gate or permissive retry is added.

The response's `x-github-dependency-graph-snapshot-warnings` header is checked on
every page using the same base64 encoding documented by GitHub. The client uses a
fixed HTTPS API origin, no redirects or ambient proxy, per-request timeout, bounded
body size and page count. It never follows a response-supplied next URL with the
credential. A three-minute workflow step deadline bounds the overall gate.

The small `dependency-review-evidence` artifact records the exact repository and
SHAs, available/unavailable status, safe failure category and snapshot warning.
It does not record the API credential or arbitrary upstream response bodies.
The job summary distinguishes availability from vulnerability/license policy.
Previous bot comments may describe an older commit: current check status and
recorded SHAs take precedence; this gate does not rewrite another action's history.

Existing NuGet audit, severity threshold, pinned action and license restrictions
remain unchanged. There is no package, product code or token-permission expansion.
The build and source-generation/format checks are not bypassed by this increment.

## What remains open

**This is not a complete fix of #110.** It prevents a false clean result but does
not create the missing snapshots or prove that every resolved NuGet dependency is
represented. No main merge or issue closure is authorized by passing unit tests.
With the current missing snapshot, a failing Dependency Review job is expected
and must remain visible; do not make it green by weakening this gate.

GitHub's supported-ecosystem table lists NuGet project files and packages.config,
not packages.lock.json. This repository uses centralized versions and committed
NuGet lockfiles. Its checked-in CI has no snapshot submission stage. Those facts
identify a graph-production gap worth investigating, but do not prove which
repository-level automatic submission settings are enabled. Such settings have
not been read or changed by this work.

Before #110 can be closed, provide matching base/head snapshots containing the
actual restored dependency graph; verify a real package change is visible; preserve
lock consistency and direct/transitive auditing; and separately review the license
policy. Prefer a supported producer with an explicit trust boundary. Do not solve
this by running untrusted PR build scripts with a write-capable repository token,
adding pull_request_target checkout of PR code, inventing success evidence, or
silently replacing a missing base snapshot with a different commit.

The pinned action still supports the deprecated `deny-licenses` option. Its warning
is not a reason to remove the policy. Moving to an allow-list is a separate policy
decision requiring an inventory and explicit review; no license-coverage guarantee
is inferred from a green action or this preflight.

## References and verification

- GitHub dependency review and snapshot-warning header:
  https://docs.github.com/en/code-security/concepts/supply-chain-security/dependency-review
- Supported ecosystems and manifest formats:
  https://docs.github.com/en/code-security/reference/supply-chain-security/dependency-graph-supported-package-ecosystems
- Exact pinned action's header handling:
  https://github.com/actions/dependency-review-action/blob/a1d282b36b6f3519aa1f3fc636f609c47dddb294/src/dependency-graph.ts
- License-option discussion:
  https://github.com/actions/dependency-review-action/issues/997

Local focused API tests use synthetic headers/responses only. The real workflow
must separately demonstrate that the actual missing-head warning blocks the action.
Final exact-commit build/test/format results and the evidence artifact belong in
PR #112. Do not describe an intentionally blocked dependency check as complete CI
success or claim that all issue acceptance criteria were met.
