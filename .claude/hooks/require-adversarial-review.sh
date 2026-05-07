#!/bin/bash
# PreToolUse hook for `git commit*` (matched via `if: Bash(git commit*)`).
# Blocks the commit unless the adversarial-review skill has been run
# against the currently staged diff — the marker file's contents must
# equal the SHA-1 fingerprint of `git diff --cached`.
#
# To mark a review complete after running the skill:
#   git diff --cached | sha1sum | awk '{print $1}' > .claude/.adversarial-review-marker
#
# To override for trivial commits (lockfile regen, typo fixes only):
#   WEAVE_SKIP_ADVERSARIAL_REVIEW=1 git commit ...
set -uo pipefail

if [ "${WEAVE_SKIP_ADVERSARIAL_REVIEW:-}" = "1" ]; then
  exit 0
fi

if ! command -v jq >/dev/null 2>&1; then
  echo "adversarial-review hook: jq not installed; cannot parse hook input. Install jq or set WEAVE_SKIP_ADVERSARIAL_REVIEW=1." >&2
  exit 2
fi

input=$(cat)
command=$(printf '%s' "$input" | jq -r '.tool_input.command // empty')

# Defensive: the `if: Bash(git commit*)` already filters, but if this
# hook is wired without that filter, only act on git commit.
case "$command" in
  git\ commit*) ;;
  *) exit 0 ;;
esac

project_dir="${CLAUDE_PROJECT_DIR:-$(pwd)}"

# Nothing staged — let `git commit` produce its own error.
if git -C "$project_dir" diff --cached --quiet 2>/dev/null; then
  exit 0
fi

# Fingerprint must match the documented marker command:
#   git diff --cached | sha1sum | awk '{print $1}'
staged_sha=$(git -C "$project_dir" diff --cached | sha1sum | awk '{print $1}')

marker="$project_dir/.claude/.adversarial-review-marker"
if [ -f "$marker" ] && [ "$(cat "$marker")" = "$staged_sha" ]; then
  exit 0
fi

cat >&2 <<EOF
adversarial-review missing for the staged diff (fingerprint: ${staged_sha:0:12})

This commit is blocked because the adversarial-review skill has not been
run against the currently staged content. Required workflow:

  1. Invoke the adversarial-review skill — see
     .claude/skills/adversarial-review/SKILL.md for the 7-category checklist.
  2. Report the punch list to the user. Fix or acknowledge each finding.
  3. Re-stage if you changed code, then mark the review complete:
       git diff --cached | sha1sum | awk '{print \$1}' > .claude/.adversarial-review-marker
  4. Re-run the commit.

Override for trivial commits (lockfile regen, typo fixes only):
  WEAVE_SKIP_ADVERSARIAL_REVIEW=1 git commit ...

Configuration:
  hook script:    .claude/hooks/require-adversarial-review.sh
  hook config:    .claude/settings.json -> hooks.PreToolUse
  marker file:    .claude/.adversarial-review-marker  (gitignored)
EOF
exit 2
