---
name: verify-weave
description: How to exercise Weave's real behaviour (mailbox-worker, auth-worker, governed external actions, the .NET host) and what evidence proves a change works; use with verification-first before claiming a change works.
---

# Verify Weave

Use this with [verification-first](../verification-first/SKILL.md). Repository rules stay in [AGENTS.md](../../../AGENTS.md); apply [check-rules](../check-rules/SKILL.md) and [adversarial-review](../adversarial-review/SKILL.md) before committing, as they say.

## Setup

- Node 24.22 or later (`node --version`). Under Node 24.11 some suites fail on `node:sqlite`. When the machine has an older Node, unpack the official `node-v24.*-linux-x64.tar.xz` into a scratch folder and put its `bin` first on `PATH`.
- `typescript/mailbox-worker`: `npm ci`, then `npm run build`. The build writes `dist/index.js`, which the auth-worker workerd tests load. **Rebuild the mailbox worker after every mailbox change before running auth-worker tests**, or they test the previous bundle.
- `typescript/auth-worker`: `npm ci`, then `npm run build`. Each workerd test loads both `auth-worker/dist/index.js` and `../mailbox-worker/dist/index.js`, so both builds must be current. The workerd tests start the real auth worker and mailbox worker in Miniflare/workerd, with service bindings between them, a local D1, a fake GitHub and a fake external-action provider (`EXTERNAL_ACTION_TEST_ENDPOINT`, test config only; never put it in deployed config).
- One mailbox-worker test file: from `typescript/mailbox-worker`, after `npm run build`:
  `node --require ./test/local-miniflare.ts --test --test-concurrency=1 test/<name>.test.ts`.
  The `--require` preload is needed; without it Miniflare's keep-alive sockets make runs flaky.
- One auth-worker workerd file: from `typescript/auth-worker`, after both builds: `node --test test/workerd-<name>.test.ts`.
- .NET: the .NET 11 SDK (preview until GA) plus the .NET 10 runtime, then the commands in AGENTS.md "Build and verification".
- Two checkouts can run tests at the same time, but two runs in one checkout race on `dist/`. Use `git worktree add` for a second checkout.

## Feature map

| Feature | Code entry points | How to exercise it for real | Evidence to capture | Neighbours at risk |
| --- | --- | --- | --- | --- |
| Governed external action: propose, owner approval page, execute, status | `mailbox-worker/src/external-actions.ts`, `owner-actions.ts`, `external-providers.ts`; `auth-worker/src/owner-page.ts`, `owner-forms.ts` | auth-worker `node --test test/workerd-owner-page.test.ts` (the `account_ApproveExternalAction_*` and `actionPage_*` tests drive both workers and the fake provider); mailbox-worker `test/external-actions.test.ts` | pass counts; the fake provider's recorded call count (exactly one send on approve, none on reject or stale session); status `completed` / `failed` / `unknown` read back | notices, publishing adapters, OpenAPI/MCP schemas, owner binding |
| Publishing adapters (Postiz, Bluesky, Telegram, Discord through `publishing.ts`; YouTube upload wired separately in `external-providers.ts`; X and LinkedIn while their PRs are open) | `mailbox-worker/src/*-provider.ts`, `publishing.ts`, `external-providers.ts`, `external-media.ts` | mailbox-worker `test/postiz.test.ts`, `test/bluesky.test.ts`, `test/telegram.test.ts`, `test/discord.test.ts`, `test/youtube-upload.test.ts` and `test/publishing.test.ts`; each fakes the platform's HTTP API in-process | requests the fake platform received (method, path, body fields), the action's final status and `failure` code; uncertain answers end `unknown` and are never re-sent | governed action flow, media retention, `failure` codes Envoylet shows |
| Inbox notices for external actions | `mailbox-worker/src/notices.ts`, `reads.ts` | mailbox-worker `test/external-action-notices.test.ts`, `test/notices.test.ts` | notice JSON shape on `GET /v1/inbox`, including `failure` (null, a pre-dispatch code, `provider-rejected: <reason>` or `outcome-unknown`, as the action read returns it); content and receipts absent | OpenAPI inbox schema, Envoylet's `/#publish` notices |
| Owner binding and key requests | `mailbox-worker/src/binding-requests.ts`, `key-requests.ts`, `agent-keys.ts`; `auth-worker/src/owner-page.ts` | mailbox-worker `test/binding-requests.test.ts`; auth-worker `test/workerd-owner-page.test.ts` (`account_ConfirmBinding_*`, `account_ConfirmKeyRequestAndRevokeKey_*`) | agent owned after a fresh confirm; stale session, cross-origin and replayed forms have no effect | agent limits, recovery codes |
| Remote MCP with OAuth, two owners end to end | `mailbox-worker/src/remote-mcp.ts`, `private-mcp-tools.ts`, `remote-oauth.ts`; `auth-worker/src/index.ts`, `consent-page.ts` | auth-worker `node --test test/workerd-mcp-e2e.test.ts` (`twoOwners_FullWorkflowOverMcp`) | pass; each owner sees only their own agents and messages | consent, client grants |
| GitHub sign-in and the optional demo allowlist | `auth-worker/src/github-login.ts`, `trial-allowlist.ts`, `human-session.ts` | auth-worker `test/workerd-github-login.test.ts`, `npm run test:portable` | open sign-in by default; malformed allowlist fails closed (AGENTS.md owner decision 2026-11-05) | owner pages, registration limit |
| Public contract: OpenAPI and MCP tool schemas | `mailbox-worker/src/openapi.json`, `private-mcp-tools.ts`, `agent-start.md` | mailbox-worker `test/openapi.test.ts`, `test/mcp-tool-contract.test.ts` | pass; the schema names every new field, status and `failure` code | Envoylet's client, agent-start guide |
| .NET Governed Tools (ToolActor, durable journal, approval) | `csharp/src/Invocations/`, `csharp/hosts/Weave.Host/` | `dotnet test --project csharp/tests/Weave.Security.Tests/Weave.Security.Tests.csproj --no-build -c Release` after the solution build, then the full solution test | pass counts per target framework | MCP HTTP fixtures in `Weave.Tools.Tests` |
| Docs checker | `scripts/docs/` | `cd scripts/docs && npm ci && npm run typecheck && npm test && npm run check` | `check` prints no findings | every docs edit |

## Gate

From the repository root, on Node 24.22+:

```bash
(cd typescript/mailbox-worker && npm ci && npm run build && npm run typecheck && npm test)
(cd typescript/auth-worker && npm ci && npm run build && npm run typecheck && npm run test:portable && npm run test:workerd)
(cd scripts/docs && npm ci && npm run typecheck && npm test && npm run check)
```

For C# changes add AGENTS.md's restore, locked restore, build and test commands. The full mailbox-worker `npm test` takes tens of minutes; start it first, in its own checkout, and run focused files elsewhere meanwhile.

## Traps

- auth-worker workerd tests load `dist/index.js` from both workers: a stale or missing bundle makes them pass or fail for the previous code, or fail with ENOENT.
- A passing provider test proves the fake platform's view. Check that the fake asserts the request body and that a refusal and an uncertain answer each have a test; never point a test at a real platform.
- A guard is verified only when a mutation (remove or invert it) makes a named test fail. List each mutation and the failing test in the PR.
- `submit-nuget` fails on every PR and on `main` (NETSDK1045, GitHub's Automatic Dependency Submission); it is not a signal about the PR.
- A hosted check that never started is not a pass.
- `npm run build` in auth-worker is a wrangler dry run; on its own it checks bundling, not behaviour, but the workerd tests then run the bundle it writes.

## Last verified

2026-11-06, `main` at `536f25e`: auth-worker portable 92/92 and workerd 93/93; mailbox-worker focused files external-actions 21/21, external-action-notices 12/12 (14/14 after #374 at `2aee50a`), telegram 7/7, publishing 3/3, binding-requests 19/19, openapi 6/6, mcp-tool-contract 4/4. The full mailbox-worker suite, the docs checker and the .NET row results are in the install PR's evidence.
