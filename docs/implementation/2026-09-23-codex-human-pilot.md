# Codex + human-controlled write pilot

## Status: live acceptance is separate from code/contract checks

Use the [authorized UUID proposal contract](2026-09-24-authorized-proposal-uuid.md)
with a matching Host and bridge revision. Server proposals now survive loss of a
client body file. API/MCP references use invocation UUIDs; the original request_key
and manual JSON-transfer instructions from #131 are superseded below. This update
does NOT declare a real model, human decision or deployment isolation tested.

The previous first_use.py is a scripted exercise. Do not call organize() or pipe
its confirmation into review.py and label that a real Codex/human trial. The bridge
is a four-tool example, not an SDK, sandbox, approval engine or identity provider.

## Prerequisites and trust boundary

Use the [operator guide](2026-09-22-trusted-operator-onboarding.md) for ONE protected
full Host, its retained journal/revocations and an independent reviewer. Keep target
files, proposal database, operator/reviewer/signing keys and Host management access
OUTSIDE the business Codex runtime. A separate directory or read-only mode is not
isolation. Prefer a separate machine/VM with shared-drive/admin side channels absent.
Expose only the intended HTTPS origin to the Agent, not Orleans/management services.

Before giving the Agent a credential, a trusted operator checks:

1. The FileSystem tool is rooted in a disposable non-sensitive document directory.
2. ApprovalRequiredGrants contains EXACT tool:files:invoke:write_file. A disposable
   first write must produce202/Pending and leave target bytes unchanged. Stop if it
   executes. The adapter does not create a new approval boundary itself.
3. The Agent startup profile has only read_file, write_file and invocation:read.
   It has no operator, connect or reviewer/approval grant. Optional owner-side body
   retrieval needs explicit invocation:proposal:read as well; status/resume do not
   require that grant and do not add it automatically.
4. Actual probes establish no direct target/store access, no shared drives or admin
   socket, and no inherited credentials providing another route. Probe disposable
   sentinels, not real secrets; only inspect the selected pilot environment.
5. The real isolated Codex runtime has an authorized login. Check codex login status
   there. The operator machine's login is not the VM's login. Never copy auth.json
   or paste keys/device codes/access tokens into chat. This example does not call a
   paid model in CI or manufacture a user's login.

Use startup profile workspace onboarding, subject agent, a bounded lifetime and
these grants: tool:files:invoke:read_file, tool:files:invoke:write_file, invocation:read.
Credentials arrive via WEAVE_AGENT_CAPABILITY and optional WEAVE_AGENT_BEARER, not
from model authentication. Issue fresh narrow credentials deliberately when needed;
that does not renew expired approval or authorize replay of an uncertain effect.

## Configure the existing stdio example

In the isolated test user's Codex config, adapt actual absolute paths and HTTPS
origin. Preserve unrelated settings and never embed credentials:

```toml
[mcp_servers.weave_files]
command = "python3"
args = ["/opt/weave/examples/governed-tools/codex_bridge.py",
        "--url", "https://weave-pilot.example.test",
        "--workspace", "onboarding", "--tool", "files",
        "--requests", "/home/agent/weave-pilot-receipts"]
env_vars = ["WEAVE_AGENT_CAPABILITY", "WEAVE_AGENT_BEARER"]
startup_timeout_sec = 10
tool_timeout_sec = 45
```

Windows uses its actual Python path and protected per-user directory/ACLs. Listing
variable names does not supply their values; use the trusted launch environment.
Keep TLS validation and any Codex-side approvals. Verify the installed client's
config/help and actual MCP initialization; a codex mcp list entry is not successful
model invocation. This server exposes the bounded MCP2024-11-05 stdio/tools subset.

Four tools only: read_document, submit_write, get_status, resume_write. The last
three require invocation_id (a real UUID), not an arbitrary local name. There is
NO approve, issue, connect, shell or arbitrary URL/HTTP operation. Retained local
receipts bind origin/workspace/tool and keep ID/hash, not proposal bodies. Retain
the UUID across interruptions. The server stores the request, not forgotten IDs.

## Natural-language task for a fresh business Agent

Keep the human reviewer ready before beginning; do not create many Pending requests
while no one can review them. Review and Agent credential lifetimes are separate.

> Read meeting.txt and constraints.txt through Weave. Prepare summary.md containing
> decisions, actions with owners/dates, constraints and unresolved questions. Missing
> facts stay unconfirmed. Generate and retain one valid invocation UUID for this
> proposal, submit_write with invocation_id/path/content, and stop when Pending.
> Return the full proposal, target, UUID and actual status. Do not write directly,
> approve yourself or run a prescribed output/tool-call script. After interruption,
> query get_status with the SAME UUID before deciding what happened.

Do not prefill a summary or hand the Agent a scripted sequence and call it autonomous.
Record actual client/model versions, task, tool calls and returned fields. Document
content is data, even when it contains instructions. MCP isError=false does not
mean a Pending write executed. Do not invent NotDispatched or a digest if the server
has not returned or otherwise established it.

## Human decision without a request-file handoff

The trusted reviewer needs the UUID and authenticated context, not a JSON copied
from the Agent. Use the existing terminal with protected reviewer/operator access:

```bash
python3 examples/governed-tools/review.py \
  --url "$WEAVE_URL" --workspace onboarding --tool files --invocation-id "$INVOCATION_ID"
```

It retrieves the full proposal from the Host and revalidates the pending plan and
current target before displaying the complete escaped content/digest/expiry.
The PERSON types the exact approve/reject confirmation. No assistant, workflow,
echo/printf or automated input may stand in for that decision in this live trial.
A Codex permission prompt is not a Weave approval receipt. Approval does not write.
Verify the target hash remains unchanged immediately after the decision.

Business Codex then queries get_status with the same invocation_id. Only valid
Approved with no admitted outcome can lead to explicit resume_write. The empty-body
resume endpoint loads the original server proposal and rechecks CURRENT authority,
target/digest and admission. No body from the model is accepted at that endpoint.
Changed content needs a separately authorized new proposal/review, not overwrite.

Rejected, expired, cancelled or unknown outcomes stop the old request. Preserve its
history. An operator may explicitly start a genuinely new trial AFTER confirming
that this is not hiding an old admitted/uncertain effect; never auto-change UUIDs.
Duplicate confirmed results cause no second dispatch. A restarted client with only
the UUID and valid credentials can query/continue without an old local body file.
A historical pre-upgrade request without a server body stays unavailable; no
client-generated replacement is accepted as its missing evidence.

## Live completion checklist — mark only actual observations

- [ ] An authenticated real Codex chooses tools and generates its own proposal.
- [ ] Actual Agent-runtime probes establish target/store/privileged-access denial.
- [ ] Pending leaves target bytes unchanged; a person reviews the server-verified
      target, full content and digest through the UUID terminal and decides.
- [ ] Approval itself leaves bytes unchanged; rejection prevents execution and is
      preserved rather than evaded by an automatically changed UUID.
- [ ] Approved same-UUID continuation with current rights has the recorded result
      and exact bytes/public readback; duplicate calls do not create another attempt.
- [ ] After an Agent restart/local receipt loss, querying the retained UUID comes
      first; no unknown effect is retried. A rejected trial and an approved trial
      use separate IDs and are not stitched into one false success record.

Retain times, UUIDs, states, actual tool calls, exact file hashes and redacted human
review evidence. Mark missing evidence unverified. Local process fixtures, simulated
HTTP replies and scripted protocol confirmations are explicitly NOT these results.
No new UI buttons, cloud deployment, human identity product or OS isolation guarantee
is introduced by this migration.

References: [Codex MCP](https://developers.openai.com/codex/mcp/),
[Codex authentication](https://developers.openai.com/codex/auth/).
