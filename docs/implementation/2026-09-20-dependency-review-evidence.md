# Dependency comparison evidence and NuGet snapshot production

## The defect and the guard

The pinned Dependency Review action can report a missing-head snapshot warning
and still conclude success. The first increment reproduced that behavior and
added a read-only preflight before the policy action. The preflight still blocks
on missing/invalid evidence on any page, HTTP/network failures and size/page limits.
A valid empty delta is not a complete vulnerability/license audit.

Comparison visibility can lag successful snapshot creation. The preflight CLI
defaults to no waiting; CI opts into a 600-second monotonic deadline with
`--snapshot-wait-seconds` (integer range 0–600). Only a valid, nonempty snapshot
warning after complete pagination and response validation permits another attempt.
Each attempt restarts page 1 for the same exact source SHAs, with a ten-second
interval and a separate cap of 61 attempts. Records from warned attempts are
discarded. HTTP/authentication, network, malformed warning/body, and limit failures
still stop immediately; warnings never become an available result.

The remaining deadline bounds each request timeout (at most 15 seconds), is checked
before each page, and rejects a response completed at or after expiry. Socket reads
are also covered by the preflight step's 12-minute runner timeout; the enclosing
review job has 20 minutes. Expiry remains a failed gate. Waiting does not resubmit
snapshots or rerun vulnerability/license policy, and does not establish that every
snapshot warning will recover.

The preflight uses the pinned action's `per_page=5` view. An observed warning-free
comparison returned six distinct records at page size 100, but seven across the
complete size-five pages (nine raw records including exact duplicates). The extra
record was a separate manifest identity; the pinned action's schema preserved it.
Using the same page size retains that record rather than relaxing exact comparison.
The preflight still constructs numbered pages on the fixed origin and source pair,
never following response-provided URLs. A single read is bounded by 400 pages,
2,000 raw records, 2 MiB per response and 40 MiB across all responses (the previous
20-page worst-case body budget). This also bounds the default zero-wait read;
CI additionally retains the deadline and runner time limits above. Exact complete
record comparison, conflicting-identity rejection and license policy are unchanged.

## Exact source graphs, separate trust boundaries

The pipeline requires two read-only validations for the exact event base/head pair.
The head must pass `dotnet restore Weave.slnx --locked-mode` without modifying any
committed lockfile. The historical base must supply a complete, authentic committed
lock graph through the same SHA-pinned reader used by the snapshot writer. The base
job checks out only that trusted reader, reads source Git objects with a read-only
token and does not execute or restore historical project code. Neither job persists
checkout credentials; failure of either blocks snapshot production.

The required-check names remain `Locked dependency graph (base)` and
`Locked dependency graph (head)`. The base step, summary and `dependency-baseline`
artifact explicitly identify its validation as `committed-lock-graph` with
`restore_performed: false`. Its graph retains the actual base SHA/ref, lock blob
identities and resolved package versions. This is evidence of the historical
committed dependency state, not a claim that the old source can restore or build.

Originally both jobs required fresh locked restores. That deadlocked lock-repair
PRs after dependency updates reached main without regenerating every consumer
lock: the repair changed the head but could not rewrite its immutable base. A base
whose committed graph remains structurally valid can be compared even when its
project declarations no longer match those locks. Base locks are neither regenerated
nor replaced with head locks; the resulting differences remain visible to policy.
Missing locks, malformed graphs, missing edges, corrupt hashes and invalid source
identities still block. This intentionally changes historical-baseline semantics;
strict head restore, full-head audit and comparison policy remain mandatory.

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
packages rather than inventing NuGet identities for local code. Every Project node
is traversed, including projects with no incoming external-package edge. Otherwise
a package referenced only by a local project could be missing without rejection.
Empty projects are permitted; incomplete/cyclic project edges and non-map dependency
lists are not. Unknown node kinds,
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
size limits and deadlines; no policy-result retry or fabricated snapshot is introduced.

## Preserved policy and limits

The existing high-severity threshold, license restrictions, NuGet audit, strict
build and format checks remain unchanged. The policy job keeps contents-read
permission; only the separate non-building snapshot writer gains contents-write.
This is not untrusted PR build execution with a write-capable token. Workflow
changes themselves remain security-sensitive and require normal maintainer review.

The submitted graph covers committed NuGet locks, not every language's dependencies
or every possible dynamic/custom build configuration. The head locked restore verifies
the normal solution configuration; it is not a sandbox against a malicious project
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
