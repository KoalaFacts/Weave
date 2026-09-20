# Dependency comparison evidence and NuGet snapshot production

## The defect and the guard

The pinned Dependency Review action can report a missing-head snapshot warning
and still conclude success. The first increment reproduced that behavior and
added a read-only preflight before the policy action. The preflight still blocks
on missing/invalid evidence on any page, HTTP/network failures and size/page limits.
A valid empty delta is not a complete vulnerability/license audit.

## Exact source graphs, separate trust boundaries

The pipeline now validates BOTH exact event base/head commits using
`dotnet restore Weave.slnx --locked-mode` in jobs with read-only repository access.
Checked-out credentials are not persisted. Their failure blocks snapshot production;
restoration does not silently regenerate or accept modified lockfiles.

A separate fresh runner executes an explicitly SHA-pinned producer. It has the
contents-write permission required by GitHub's snapshot API, but does NOT check
out, execute, restore or build the inspected PR revision. It reads the two exact
commits and their trees/lockfile blobs using fixed-origin GitHub API GET requests.
No PR-produced executable or untrusted artifact is downloaded/executed by that job.
Changes to the producer require reviewing and explicitly updating its immutable
pin in the workflow; a modified script on a PR does not silently replace it.

The producer inventories every checked-in .NET project and requires its committed
NuGet lockfile. It verifies Git blob hashes, complete trees, package content hashes,
resolved package versions, direct/transitive classifications and dependency edges.
Framework graphs remain distinct. Local Project edges expand to actual external
packages rather than inventing NuGet identities for local code. Unknown node kinds,
missing locks/edges, ambiguous names, symlinks, corrupt/oversize JSON and empty total
graphs fail closed. All packages are included in policy evaluation; the conservative
runtime scope is not a claim that test-only dependencies are production runtime.

Only validated JSON snapshots are submitted, tagged with their actual source SHA,
ref and consistent detector/correlator. Both graphs are fully constructed before
submission. SUCCESS and ACCEPTED receipts are supported, matching GitHub's official
dependency-submission toolkit; ACCEPTED is normal for a nondefault branch. A receipt
is NOT comparison evidence. The original warning-sensitive preflight must still
succeed, followed by the pinned vulnerability/license policy action. Partial
submission or an API failure does not make the review pass.

Artifacts retain the exact submitted graphs, lock blob identities, source SHAs,
manifest/node counts, receipt IDs and the independent comparison result. Credentials
and arbitrary server bodies are not logged. API calls use HTTPS, no proxy/redirect,
size limits and deadlines; no retry-to-green or fabricated snapshot is introduced.

## Preserved policy and limits

The existing high-severity threshold, license restrictions, NuGet audit, strict
build and format checks remain unchanged. The policy job keeps contents-read
permission; only the separate non-building snapshot writer gains contents-write.
This is not untrusted PR build execution with a write-capable token. Workflow
changes themselves remain security-sensitive and require normal maintainer review.

The submitted graph covers committed NuGet locks, not every language's dependencies
or every possible dynamic/custom build configuration. Locked restores verify the
normal solution configuration; they are not a sandbox against a malicious project
build. The writer relies on no execution of such code. Existing non-NuGet static
analysis and example verification are not replaced by this detector.

Fork PRs without write permission will fail the submission gate rather than silently
skip evidence; a maintainer-controlled submission path would need its own trusted
workflow. No pull_request_target escalation or repository settings change is made.
Manual/merge-group events retain their prior policy scope; this increment's exact
pair production applies to PR and main-push events. A missing/deleted source ref
or absent base is an explicit failure, not permission to substitute another SHA.

An empty comparison of identical NuGet graphs demonstrates current evidence
availability, not an exhaustive security certification or proof of every license.
A separate known-change validation is needed before claiming full closure of the
broader #110 acceptance; its result and any remaining scope belong in the issue/PR
record. No library update or historical downgrade is required by this change.

## Verification history

The missing-producer workflow tests first failed (three intended failures, 61
controls passing). Translator tests cover direct/transitive/project edges, framework
coverage, changed versions, integrity and unsafe inputs. Receipt tests reproduced
rejection of ACCEPTED while unknown responses remain failures. Real runs validate
both exact locks, submit real source graphs and then call the actual comparison API;
final exact-head evidence is recorded on #112. Tests and API receipts are distinct.

The producer does not change product runtime, tokens, approval, schema or dependencies.
Concurrent AgentOnly composition is preserved when integrating current main.
No production deployment, credential rotation or data reset is part of this work.

References: [GitHub dependency submission](https://docs.github.com/en/rest/dependency-graph/dependency-submission),
[official submission toolkit](https://github.com/github/dependency-submission-toolkit/blob/main/src/snapshot.ts),
[dependency review](https://docs.github.com/en/code-security/concepts/supply-chain-security/dependency-review).
