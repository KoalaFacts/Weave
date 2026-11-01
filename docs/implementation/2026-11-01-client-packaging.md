# Client packaging and agent key helpers - 2026-11-01

This record covers onboarding slice 6: a Claude Code plugin manifest, the
Codex connection route, two agent key helpers and the onboarding documentation.
It is source and local verification. **Nothing has been deployed**; the OAuth
GitHub sign-in path becomes live only after the slice-7 rollout. The documented
commands were source-checked, not run against a deployment.

## What it adds

- `plugins/weave/.claude-plugin/plugin.json` declares the HTTP MCP server
  `https://demo.weave-389.workers.dev/mcp` and shares `plugins/weave/skills`;
  `.claude-plugin/marketplace.json` lists the plugin. Claude Code: install from
  the repository marketplace or `claude mcp add --transport http weave <url>`,
  then authenticate with `/mcp`.
- Codex: `codex mcp add weave --url <url>` then `codex mcp login weave`. The
  web-targeted Codex manifest still bundles no MCP server.
- `typescript/agent-key` (Node 24, no runtime dependencies) and
  `python/weave-agent-key` (Python 3.12+, `cryptography`) implement the key
  protocol: `init`, `register`, `token` (expiry only unless `--print-token`) and
  `whoami`. Differences: the Python helper is synchronous, uses a 30 s request
  timeout and applies a stricter origin check. Both wait out one 429 whose
  `Retry-After` is at most 60 s and report a longer one.
- The skills use the remote tool names and the `message` / `idempotencyKey`
  contract of the [MCP record](2026-11-01-mcp-a2a-messages.md). Owners manage
  agents, clients and keys on `/account` ([owner grants](2026-11-01-owner-grants.md)).
- Agent Start guide version 7 points section 2 at the helpers.

## Preserved

- The key protocol, token lifetime, DPoP and `private_key_jwt` rules of the
  [agent key record](2026-10-31-agent-keys.md); the HTTP `/v1` contract.
- The `scripts/marketplace` rule that the Codex manifest has no `mcpServers`.
- The empty registered-app map of the ChatGPT preview.

## Deliberately changed

- The README and plugin guide lead with the connection routes instead of the
  removed trial. Agent Start no longer says helpers do not exist; they exist
  but are not published to npm or PyPI.

## Removed

- The statement that no key helper library exists.

## Verification limits

- The Claude plugin passed `claude plugin validate`; it was not installed live.
- No `claude plugin install`, `/mcp` sign-in or `codex mcp login` was run.
- The new CI jobs have not yet run on GitHub.
- The Python helper is checked against wire vectors from the TypeScript side,
  not against a live Worker.
- Neither helper is published; the OAuth sign-in is not deployed.
