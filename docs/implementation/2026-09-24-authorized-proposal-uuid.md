# Authorized UUID proposal storage, review and continuation

## Scope

This completes the agreed four-part change: server proposal persistence, authorized
UUID APIs, existing terminal/MCP clients, and recovery/denial acceptance. It extends
Invocations and the existing SQLite journal. No new approval engine, file platform,
identity provider, background replay or universal storage layer is introduced.

An invocation UUID identifies a request; it is never a credential. Existing
workspace/tool routing stays in place. This increment does not convert every
workspace, tool or filesystem path into a new resource UUID.

## Stored content and admission

ToolActor snapshots the normalized ORIGINAL operation, parameters and RawInput
before secret substitution. The existing journal records the proposal in a separate
`invocation_proposals` table in the SAME database/transaction as the pending approval
or admitted intent/attempt. It does not add response-body caching or put proposal
content in audit/log messages. A failed proposal/attempt write rolls back the new
transaction and blocks dispatch; no successful Pending is returned without its
new proposal. Existing metadata and canonical fingerprint ownership are retained.

The proposal stores its workspace, invocation UUID, original subject, tool,
operation, input digest, connector kind and bounded serialized input. Reads compare
its SHA-256 and canonical input digest with the authorized retained metadata.
No update/upsert API exists. A different body cannot overwrite a UUID or reuse its
approval. These checks detect damaged/mismatching data, not an attacker capable of
rewriting the protected database and all evidence consistently.

Only the input snapshot is stored, not authentication headers, token envelopes or
operator/reviewer/signing keys. Resolved secret-substitution values are not copied
into it. Literal secrets placed by a caller INSIDE its submitted input would still
be input content: this feature does not automatically remove or encrypt them.
Use non-sensitive pilot inputs and secret references; retain the existing rule
that secret-resolved effective inputs cannot be presented as a verified plaintext
review when they differ from the original proposal.

**Deployment change:** the database now contains confidential proposal bodies,
not only metadata. Protect it and its WAL/SHM files, snapshots and backups outside
all Agent tool roots. Apply restrictive OS access and the deployment's existing
at-rest protections. There is no new encryption/key-management subsystem here.
Serialized proposal rows are limited to8MiB; existing HTTP request limits remain
unchanged. This is not a tenant-wide disk/memory quota.

Proposals are retained with the journal; no automatic expiry-based deletion or
retention worker is added. Approval expiration removes execution eligibility, not
the historical record. Account for growth and apply deliberate backup/retention
procedures; do not delete bodies to obtain a successful retry. No production data
migration, backup deletion or reset is performed by this code change itself.

## Routes and permissions

Prefix: `/api/workspaces/{workspaceId}/tools/{toolName}/invocations`.
HTTP route UUIDs use the existing nonzero32-hex representation. The terminal and
MCP clients accept canonical hyphenated UUIDs too and normalize them before HTTP.
Initial POST keeps the original input shape; retain its UUID BEFORE sending.

| Method and suffix | Caller and behavior |
| --- | --- |
| POST prefix | Existing exact invocation authority; submit UUID and input once. Persist before Pending/dispatch. |
| GET /{id}/proposal | Original subject; BOTH invocation:read and invocation:proposal:read. Returns stored input, not an approval or current target verification. |
| GET /{id}/approval/review | Independent reviewer; invocation:read, approval:decide and exact tool operation approve grant. Revalidates pending state, current target, effective input and expiry before returning the verified review. |
| POST /{id}/decision | Same reviewer requirements; body contains only decision and planDigest. Fetches original input server-side and uses the existing reviewed decision path. |
| POST /{id}/resume | Original subject; invocation:read plus CURRENT exact invoke grant. Empty body. Loads original input and uses the existing ToolActor authorization/approval/admission path. |
| GET /{id} and /{id}/approval | Existing owner-scoped status routes; metadata is not proposal-read authority. |

UUID-only GET/resume reject supplied bodies and query parameters. Decisions reject
unexpected JSON fields; clients cannot smuggle replacement input into the command.
Operator mode still guards reviewer/decision endpoints, and configured global API
authentication is additive. AgentOnly exposes five Agent operations, not reviewer,
decision, provisioning or other administrative routes. UUID reads/resumes carry
explicit Agent endpoint metadata; reviewer routes do not acquire that exemption.

Proposal access authenticates and authorizes entry before metadata lookup, checks
owner/workspace/tool and exact operation before loading body bytes, then rechecks
current authority after I/O. Cross-owner reads are not-found; denied/invalid
credentials remain403/401. An independent reviewer cannot approve its own request.
No new body-read grant is automatically added to existing credentials.

A proposal retrieval is a historical input snapshot, not permission to execute.
A decision body is for example `{ "decision": "approve", "planDigest": "approval-v1:..." }`.
The actual verified digest is required. Approval itself does not dispatch. Resume
uses current Agent credentials, never a saved old token or the reviewer's rights.
Repeated/concurrent resumes still pass through the one-attempt claim; confirmed or
unknown retained outcomes cannot be replayed under a new identity.

## Terminal and MCP usage

The preferred terminal invocation no longer requires a copied request file:

```bash
python3 examples/governed-tools/review.py \
  --url https://your-configured-host \
  --workspace onboarding --tool files --invocation-id YOUR_ACTUAL_UUID
```

Provide reviewer/operator credentials through the protected environment as described
in the [operator guide](2026-09-22-trusted-operator-onboarding.md), never command
arguments or chat. The helper GETs a verified review, displays its full escaped
contents and waits for a person's exact approve/reject confirmation. It sends only
the decision and digest to the UUID endpoint and does not execute the tool.

The explicit `--request` mode and POST original-body review/decision endpoints
remain available to the existing diagnostic walkthrough and read-only Dashboard.
They are not an automatic body fallback for the UUID routes and do not backfill
missing proposal rows. Their existing verification requirements remain enforced.
This is an intentional retained diagnostic mode, not a new Legacy subsystem.

The four MCP tools stay read_document, submit_write, get_status and resume_write.
The last three now require `invocation_id`, NOT request_key. Submit carries path
and content once; status/resume accept only UUID. A first submit saves an id/hash
receipt before its POST. Repeated submission with that receipt is query-only; a
changed hash conflicts. Receipt files contain no proposal body. A fresh client
with an empty receipt directory can query/resume an already stored proposal by UUID.
Keep a durable reference to the UUID; the service does not supply a list/search UI.

The bridge still has no approve, issuance, connect or arbitrary HTTP tool. Its
status checks do not create authority: the Host's final checks remain decisive.
No automatic POST retry is introduced. A lost response is unconfirmed, not proof
that the effect did not occur. Update old prompts/configured tool arguments to
invocation_id and keep historical alias/body files only as evidence, not new writes.

## Upgrade and recovery

The previous database uses user_version0. An additive immediate transaction creates
the proposal table and records version1; all earlier invocation/approval/attempt
rows remain. It never invents historical input. Version1 with a missing proposal
table is a startup fault, not an instruction to recreate empty evidence. Unknown
schema versions also fail. Keep RequireExistingStorage=true for retained deployments.

Take a normal protected, consistent backup and stop the Host before upgrading a
real deployment. Do not mix old writers with the new schema or downgrade against
the live upgraded file; use an explicit backup/recovery procedure instead. This is
single-Host migration, not distributed online schema coordination.

Historical records without bodies remain queryable through the existing status
paths. UUID body/review/resume returns409 proposal-unavailable rather than accepting
a replacement. Corrupt or unavailable storage returns503. Neither case deletes
history or authorizes a new-ID retry. New requests persist bodies normally.

## Acceptance and limits

Real Kestrel/Orleans/SQLite tests cover body loss, separate Host instances,
concurrent resume, exact content, independent authorization, cancellation/expiry
constraints inherited from the governed path, and same-ID repeated results.
Fault injection modifies ONLY isolated test databases to exercise rolled-back
proposal/attempt writes, corrupt/missing rows, old-schema upgrades and retained
versioned-table loss. Corrupt-body controls establish denial happens before body
loading rather than passing because there is no data to read.

The scoped bridge workflow runs an ordinary Host process, formal operator HTTP
setup, a new empty client directory after Host restart, a native terminal UUID
review, MCP UUID continuation and one recorded write attempt. Its confirmation
is explicitly SCRIPTED protocol verification, not live Codex/human acceptance.
The [live pilot guide](2026-09-23-codex-human-pilot.md) retains separate unchecked
human/model/isolation evidence. No production deployment or user decision is
implied by CI. Exact failing-first/final-commit evidence belongs on PR #132.
