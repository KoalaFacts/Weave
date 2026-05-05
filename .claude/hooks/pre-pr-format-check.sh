#!/bin/bash
# PreToolUse hook for mcp__github__create_pull_request.
# Runs `dotnet format --verify-no-changes` with the same exclude as CI's
# Code Quality job. On failure, blocks the PR creation and surfaces the
# diagnostic so it can be fixed before opening a PR.
set -uo pipefail

cd "${CLAUDE_PROJECT_DIR:-.}"

if ! command -v dotnet >/dev/null 2>&1; then
  # No SDK available — don't block; CI will still gate format.
  exit 0
fi

output=$(dotnet format Weave.slnx --no-restore --verify-no-changes \
  --exclude src/Foundation/Weave.Shared/ 2>&1)
status=$?

if [ $status -eq 0 ]; then
  exit 0
fi

reason=$(printf 'dotnet format --verify-no-changes failed. Fix with:\n  dotnet format Weave.slnx --no-restore --exclude src/Foundation/Weave.Shared/\n\n%s' "$output")

jq -n --arg reason "$reason" '{
  hookSpecificOutput: {
    hookEventName: "PreToolUse",
    permissionDecision: "deny",
    permissionDecisionReason: $reason
  }
}'
