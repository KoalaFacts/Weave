# License evidence and fork-permission acceptance (#110)

This completes the two remaining supply-chain acceptance items in PLAN.md. It
keeps the existing dependency action, deny list, vulnerability threshold and pinned
snapshot producer. No package version or product runtime changes are involved.

## License evidence, not a new license engine

The pinned `actions/dependency-review-action` revision
`a1d282b36b6f3519aa1f3fc636f609c47dddb294` supports the configured `deny-licenses`
input. Its implementation rejects forbidden licenses and unresolved SPDX values,
but only prints `unlicensed` dependencies (null or NOASSERTION) without failing.
The isolated test-first run reproduced both false-success cases with the actual
shipped action bundle; permitted MIT, forbidden GPL/AGPL, invalid SPDX, complete
empty delta and missing-snapshot controls established their distinct behavior.

CI now passes the action's `invalid-license-changes` output to
`scripts/check_dependency_license_evidence.py`. The small gate treats missing,
malformed or oversized output as unavailable; forbidden output as blocked; and
unlicensed/unresolved entries as unavailable. Only complete empty invalid lists
pass this gate. SPDX parsing and the deny policy remain owned by the original
action, not a second hand-written license rules engine. The action must also
succeed: a clean license output cannot turn an earlier vulnerability failure green.
No `continue-on-error`, license exemption or warning-only fallback is introduced.

`dependency-license-evidence` contains sanitized counts and status, not arbitrary
package metadata. Unknown means review is incomplete, not a legal conclusion
about that package. Correct the package metadata or obtain a reviewed policy
decision; do not populate an empty output by hand or suppress the failing gate.
Scope remains the added dependencies selected by the existing action policy,
not a repository-wide legal audit of all historical dependencies.

## Isolated action acceptance

`Dependency Policy Acceptance` checks out the SAME immutable action revision and
runs its actual `dist/index.js` against a loopback-only fixture API. Child processes
receive a synthetic token and an allowlisted environment, not the job's real
credentials or artifact tokens. Fixtures are not posted to GitHub's dependency
graph, installed in the product, or sent to license lookup services. There is no
npm installation or rebuild of a substitute action.

The runner uses the hosted Node executable and records its version in CI. The
production action uses its declared Actions runtime; this fixture establishes the
pinned policy behavior on its recorded Node version, not every runtime version.
Existing evidence-parser unit tests and the loopback warning case keep missing,
partial and malformed comparison evidence non-passing. Runtime errors or an action
that never reaches the fixture API cannot count as successful policy acceptance.

## Fork permissions: already fail closed, now verified

Under normal GitHub fork-PR settings, GITHUB_TOKEN write permissions are reduced
to read-only. Snapshot submission requires repository contents-write permission.
The new independent job uses an ACTUAL contents-read Actions token, executes only
the already-reviewed producer at `c36ab57b5c2c46e00b7cc71fdab19cac4e344b3e`, and reads
real exact base/head locks. It attempts the actual API call and requires **HTTP403,
producer exit1, status unavailable and zero confirmed submissions**. A network
failure, malformed graph, missing token, unexpected successful write or empty
artifact does NOT satisfy the test. It captures the exact source SHAs and result.

This is a live fork-equivalent permission test, not an actual external fork PR or
a check of every repository's administrative settings. No external fork, write-token
setting or repository permission was created/changed. Approval to run a fork's
workflow is not a grant of snapshot write permission.

Because the existing producer already reports this failure correctly, no extra
fork preflight, second snapshot service or privileged recovery workflow is needed.
The normal writer remains pinned and separated from read-only source restoration.
The original fork's missing-evidence gate remains non-passing; never mark it clean
because the isolated denial TEST itself passed.

## Maintainer-safe path for a reviewed fork contribution

Keep fork workflow permissions restricted. Do NOT switch to pull_request_target,
pass a personal token to fork code, rerun a fork build under write credentials,
or enable "send write tokens" merely to make dependency submission pass.

Use a maintainer-controlled integration PR for the EXACT reviewed commit:

1. Read the original PR's repository, head SHA and base SHA using the GitHub API.
   Record the full SHA. Review the diff as data, especially workflows, local actions,
   scripts, MSBuild files and imports, generated locks and executable hooks. Do not
   run source/build commands in a shell holding a write credential. Approval of a
   workflow change is a real maintainer trust decision, not just a green test badge.
2. After review, re-read the original head and ensure it has not moved. Create a
   new branch IN THE BASE REPOSITORY pointing at that exact existing commit object.
   Do not checkout/build the fork with the write token; creating a Git ref through
   the API only changes the reference. Do not silently cherry-pick/rebase to a new
   identity and reuse old evidence.
3. Open a normal integration PR from that branch to main, linking the original PR
   and reviewed SHA. Existing CI performs isolated contents-read restores of its
   exact base/head, the pinned non-building snapshot writer, evidence preflight,
   the policy action and the complete-license gate. The normal contents-write job
   runs only the reviewed pinned producer, not the integration revision's build.
4. Require the actual integration checks and source SHA comparison before merging.
   If the original head, integration head or relevant base changes, review and
   regenerate evidence; do not copy another check run's success. Preserve the
   original author/commits and link the final integration result back to the fork PR.
   The original fork gate is not waived or forged; it is superseded by a clearly
   identified reviewed integration, not falsely relabeled as successful.

Example API-only commands for a trusted maintainer shell (not an automated job):

```bash
# Set these after reviewing the original PR. SHA is the full reviewed commit.
REPO=KoalaFacts/Weave
PR=1234
SHA=<full-reviewed-40-hex-head>
# Verify the head again immediately before creating the integration ref.
test "$(gh api "repos/$REPO/pulls/$PR" --jq .head.sha)" = "$SHA" || exit 1
BRANCH="review/fork-$PR-${SHA:0:12}"
gh api --method POST "repos/$REPO/git/refs" -f "ref=refs/heads/$BRANCH" -f "sha=$SHA"
gh pr create --repo "$REPO" --base main --head "$BRANCH" \
  --title "Integrate reviewed fork PR #$PR" \
  --body "Reviewed source: $SHA. Original PR: #$PR. Require fresh exact-ref CI before merge."
```

Replace placeholders before running; never execute this block against an arbitrary
unreviewed PR. These commands were source-checked, not used to create a fake fork
in this change. #126 itself exercises the ordinary maintainer-branch validation
path, while the separate real read-only job proves the permission-denial side.

## Closeout evidence and limits

Exact failing-first, passing, merged-main runs and artifact hashes are recorded on
#126/#110. The work is complete only after the normal reviewed merge and actual
main verification. Prior real package-change evidence from #123 remains valid;
this increment does not reopen that completed gate or start the later recovery work.

No security setting, producer pin, permission scope, real credential, dependency
version, release, deployment or stored product data is changed. Unknown license
handling is an evidence gate, not legal advice or blanket license compatibility.

References: [pinned action license decisions](https://github.com/actions/dependency-review-action/blob/a1d282b36b6f3519aa1f3fc636f609c47dddb294/src/main.ts),
[fork permission adjustment](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax),
[snapshot API permissions](https://docs.github.com/en/rest/dependency-graph/dependency-submission).
