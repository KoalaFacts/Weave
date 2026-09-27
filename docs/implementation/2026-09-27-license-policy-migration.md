# Dependency license policy after the deny-list deprecation

The pinned Dependency Review action warns that `deny-licenses` is
[deprecated](https://github.com/actions/dependency-review-action/issues/938).
Removing the input alone would stop enforcing Weave's configured GPL restriction.
Replacing it with `allow-licenses` would change the decision for every license
not named in the allow list. This migration retains the existing deny decisions
without relying on the deprecated input.

The action still checks high-severity vulnerabilities and emits its complete
`dependency-changes` and `invalid-license-changes` outputs. The existing Python
evidence guard rejects missing, unresolved, or malformed action license output.
The new Node policy check evaluates each added dependency's SPDX expression with
the same `@onebeyond/spdx-license-satisfies` evaluator used by the
[pinned action](https://github.com/actions/dependency-review-action/blob/a1d282b36b6f3519aa1f3fc636f609c47dddb294/src/spdx.ts).
It rejects GPL-2.0, GPL-3.0, and AGPL-3.0 according to that evaluator. It also
rejects invalid, empty, and `NOASSERTION` licenses. A raw null license fails
closed, including when the action could otherwise look up a repository license;
this is a deliberate stricter outcome because the action's change output does
not include the looked-up license needed to verify the deny decision.

The policy check requires the number of action changes to equal the already
checked GitHub dependency-comparison count. Missing, truncated, malformed, or
oversized output cannot pass. Its report contains only counts and a failure
category, never package names or raw license text. Removed dependencies do not
create a new license denial. A complete empty delta can pass, but does not
establish repository-wide license coverage.

The action's PR summary comment is disabled because it cannot include the
later policy decision and could incorrectly report a clean license review.
The CI job status remains the gate; no PR write permission is needed for it.

The Dependency Policy Acceptance workflow runs the pinned action against a
loopback comparison and then runs both evidence and license-policy checks. It
covers permitted and prohibited expressions, invalid and missing licenses,
empty deltas, and missing snapshot evidence. The action's deprecation warning
must be absent. This is a PR-change gate, not a legal assessment of historical
dependencies or every possible SPDX expression.
