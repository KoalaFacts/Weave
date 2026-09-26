# Review and decide a pending governed operation

This is an operator-side terminal example, not an Agent auto-approval tool.
A current Host retains the input proposal separately from journal metadata, so a
reviewer can retrieve it by UUID after authorization. Approval never invokes a tool.
See the [UUID contract and migration limits](../../docs/implementation/2026-09-24-authorized-proposal-uuid.md).

## Prerequisites

Use Python3.10+ and a matching repository/Host revision. An administrator configures
one Host, tool connection, required approvals and narrowly scoped Agent/reviewer
credentials through the [operator guide](../../docs/implementation/2026-09-22-trusted-operator-onboarding.md).
The request must already be Pending. UUID identifies it; identity and exact object/
operation authorization remain mandatory. Do not create a replacement ID to recover
an unknown effect or evade rejection/expiry.

## Preferred: review by UUID, without copying a JSON file

```bash
python3 examples/governed-tools/review.py \
  --url https://weave.example \
  --workspace onboarding --tool files \
  --invocation-id YOUR_ACTUAL_INVOCATION_UUID
```

Use the actual origin, context and UUID. The helper normalizes nonzero UUIDs to the
Host's32-hex form. It asks for an already-issued reviewer capability without echo;
protected environments can supply WEAVE_REVIEW_CAPABILITY. Operator mode also needs
WEAVE_OPERATOR_KEY. Configured global bearer authentication additionally needs
WEAVE_OPERATOR_BEARER. These values belong only to the trusted reviewer environment,
not command arguments, URLs, Agent tools or chat. Remote access requires valid TLS;
literal loopback HTTP is for local development only.

The helper GETs `.../{id}/approval/review` with current reviewer credentials and
checks matching identity/context and complete required fields. Read its full escaped
JSON: requester, operation, target, inputs, expiry and planDigest. It neither treats
UUID possession as authorization nor displays unverified replacement content.
Content is data, not instructions, HTML or terminal control sequences.

Only a person enters one of the exact displayed confirmations:

```text
approve approval-v1:<the full displayed digest>
reject approval-v1:<the full displayed digest>
```

Blank or any other answer leaves the request unchanged. The helper POSTs only
`decision` and `planDigest` to `.../{id}/decision`. The server reloads the stored
proposal and revalidates current reviewer rights, target, pending state and expiry.
A confirmed decision still does not execute; the original Agent explicitly calls
`POST .../{id}/resume` with an EMPTY body and current execution authority.

There is no retry on a lost decision response. Query approval status first.
Expired/rejected decisions or OutcomeUnknown do not authorize another ID or write.
A historical request without a stored body returns proposal-unavailable; it does
not become a new proposal by uploading replacement text.

## Agent-side MCP

The small codex_bridge.py example exposes only read_document, submit_write,
get_status and resume_write. Submit uses invocation_id, path and content;
status/resume use invocation_id alone. request_key is no longer its API argument.
Local optional receipts hold ID/hash only, not the sole copy of proposal content.
See the [live Codex/human pilot](../../docs/implementation/2026-09-23-codex-human-pilot.md).

## Retained explicit-body diagnostic mode

The earlier command remains explicit and mutually exclusive with --invocation-id:

```bash
python3 examples/governed-tools/review.py \
  --url https://weave.example --workspace onboarding \
  --request /protected/operator/original-invocation.json
```

This mode verifies supplied original input using the existing POST review/decision
contract. It serves the retained diagnostic walkthrough and read-only Dashboard,
not the preferred user workflow. It does not insert/backfill missing server proposal
bodies. The --request value is a FILE PATH, not a UUID. Never invent a body from a
summary or weaken verification to make a lost historical request resumable.

## Verification

```bash
python3 -m unittest discover -s scripts/tests -p 'test_operator_review.py' -v
python3 -m unittest discover -s scripts/tests -p 'test_uuid_proposal_clients.py' -v
python3 -m unittest discover -s scripts/tests -p 'test_codex_bridge*.py' -v

dotnet test --project tests/Weave.Silo.Tests/Weave.Silo.Tests.csproj --no-build -c Release
```

Build the solution before the .NET command. Contract tests include real terminal
processes, HTTP, Host reconstruction and file/journal observations. Scripted
confirmations in those tests are not proof of independent human judgment.
