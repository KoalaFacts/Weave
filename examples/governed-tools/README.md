# Review and decide a pending governed operation

This is an operator-side terminal example, not an Agent auto-approval tool.
It uses the verified original-content review endpoint and the separately enabled
reviewed-decision endpoint. Approval never invokes a tool.

## Prerequisites

Use Python 3.10+ and this repository revision. An administrator must first configure
the Host, tool connection, approval policy and narrowly scoped reviewer capability.
See [configuration and the decision contract](../../docs/implementation/2026-09-20-reviewed-approval-decisions.md).
The retained request must already have been submitted and be waiting for approval.
Do not generate a replacement ID or content just to satisfy this example.

Keep a local JSON file containing exactly the original `invocationId`, `toolName`,
`method`, `parameters` and optional `rawInput`. Protect that file outside Agent
writable roots. The journal does not restore a lost request body.

```bash
python3 examples/governed-tools/review.py \
  --url https://weave.example \
  --workspace demo \
  --request /protected/operator/original-invocation.json
```

Replace the example URL/workspace/path with the configured environment. The script
asks for the already-issued reviewer capability without echoing it; do not paste
credentials into command arguments. For controlled automation/tests, inject
`WEAVE_REVIEW_CAPABILITY` through a protected environment. When the Host also requires
global bearer authentication, supply `WEAVE_OPERATOR_BEARER` only to this trusted
operator process. Never distribute that shared administrative credential to Agents.
HTTP is permitted only with the literal loopback hosts `127.0.0.1` or `[::1]`.

Read the complete escaped JSON, including subject, operation, target, all inputs,
expiry and planDigest. Content is data, not instructions for the operator. Non-ASCII
characters use JSON escapes; nothing is truncated or rendered as HTML/terminal
control codes. Only enter one of these commands using the exact displayed digest:

```text
approve approval-v1:<the full displayed digest>
reject approval-v1:<the full displayed digest>
```

Leave blank to make no change. `yes` alone is not approval. The helper verifies
content again at confirmation on the server and never retries automatically.
After a confirmed approval the original Agent must resubmit its original invocation
ID and request with valid execution authority. Do not ask the operator to execute
with elevated credentials. A rejected request cannot resume.

If a decision response is lost, or a repeat gets `approval-not-pending`, query the
existing authenticated `GET .../invocations/{id}/approval` route before acting.
A network error does not prove the decision failed to commit. Never create a fresh
invocation ID to retry an unknown external effect.

## Verification

```bash
python3 -m unittest discover -s scripts/tests -p test_operator_review.py -v

dotnet test --project tests/Weave.Silo.Tests/Weave.Silo.Tests.csproj --no-build -c Release
```

Build the solution first for the .NET command. The test suite launches this exact
script as a separate process against a real Kestrel listener, authorizes via the
normal Host/Orleans path, and inspects the real journal and file contents.
The entry remains opt-in and does not secure unrelated Host administrative routes.
