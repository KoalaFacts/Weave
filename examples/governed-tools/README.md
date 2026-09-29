# Governed tools with Node.js

## See the flow in memory

From the repository root, with Node.js 22.18.0+:

```bash
node examples/governed-tools/demo.ts
```

Node runs these `.ts` files through built-in type stripping, without a
transpiler or npm install; it does not type-check them. The document, grants,
proposal, decision and attempt count stay in memory. It creates no files,
starts no Host and makes no network calls. It illustrates the decisions; it
does not verify Weave security.

## Exercise the real Host

With the repository's [.NET SDK](../../global.json) installed and ports 9401
(HTTP), 11111 (Orleans silo) and 30000 (Orleans gateway) free, run:

```bash
dotnet build hosts/Weave.Host/Weave.Host.csproj -c Release
node examples/governed-tools/host-demo.ts
```

No npm packages or Python are needed. The Node script creates a disposable local
Host configuration and sample files, obtains separate Agent and reviewer
capabilities, and calls the real public HTTP endpoints. It checks that a read-only
Agent cannot write, a pending write has no effect, approval alone has no effect,
the original caller can resume, and the same invocation ID does not write twice.
It removes its temporary files when finished. The Host uses its on-disk SQLite
journal; this path is not all in memory. The reviewer confirmation is scripted,
so this run does not prove independent human judgment or restart recovery.

## Review a real pending request

An operator must first configure the Host, tool, required approval and narrowly
scoped Agent/reviewer credentials using the [operator guide](../../docs/implementation/2026-09-22-trusted-operator-onboarding.md).
In the trusted reviewer environment, provide `WEAVE_REVIEW_CAPABILITY` and
`WEAVE_OPERATOR_KEY`. If global bearer authentication is configured, also provide
`WEAVE_OPERATOR_BEARER`. Keep these values out of command arguments, URLs,
Agent tools and chat. Then run:

```bash
node examples/governed-tools/review.ts --url https://weave.example --workspace onboarding --tool files --invocation-id YOUR_ACTUAL_INVOCATION_UUID
```

The helper retrieves the Host-retained proposal by UUID, checks its context and
required fields, and displays the exact target, inputs, requester, expiry and
plan digest. The content is data, not instructions. Only typing the displayed
`approve approval-v1:...` or `reject approval-v1:...` records a decision.
Approval does not execute the tool. The original Agent must separately call
`POST .../{id}/resume` with an empty body and current execution authority.
Do not issue a new ID to work around a rejected, expired or unknown outcome.
See the [UUID contract](../../docs/implementation/2026-09-24-authorized-proposal-uuid.md)
for the response and migration limits.

The existing Codex MCP bridge remains in `codex_bridge.py`; it exposes Agent
read, submit, status and resume operations, not reviewer approval. Its
[pilot record](../../docs/implementation/2026-09-23-codex-human-pilot.md)
describes the separate human decision boundary.
