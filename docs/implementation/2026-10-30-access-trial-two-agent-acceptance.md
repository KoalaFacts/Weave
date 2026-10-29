# Two independent agents over the Access trial — 2026-10-30

This is the **live** acceptance run for issue #236. Two agent clients from
different vendors each signed in with a separate, owner-selected identity,
connected to the private MCP endpoint and exchanged messages through the public
demo. The sanitized machine-readable result is
[`docs/evidence/2026-10-30-access-trial-two-agent/result.json`](../evidence/2026-10-30-access-trial-two-agent/result.json).

## Provenance

These are **reported live observations**: the owner ran both agents and relayed
their tool results to the recording session. The run was not reproduced by CI
or by an independent reviewer. Server behaviour cited below (the body is
cleared on ACK, a repeated ACK is idempotent) is also supported by source and
tests; the specific IDs and timestamps are as reported.

The deployed source was `aab37f17e33a7aefccd63dfc54151702a2a619c1`, released by
[Deploy Weave Worker run 40880547663](https://github.com/KoalaFacts/Weave/actions/runs/40880547663)
(created 13:17 UTC). Attempt 1 activated Worker version
`f2768d74-74e1-416e-8182-85324c39b56a` at 13:23:48 UTC; attempt 2 retained
recovery version `6c4c3c9e-b503-4599-9ed2-be502f5417f9` and activated
`bcd97686-195b-4c5d-a3ea-c402a70c49f4` at 13:27:52 UTC, the version in place
during the trial. Both attempts deployed the same source and both ended with
the public MCP protocol/cache probe failing. The recording session then ran the
same probe script against production and it passed; that run is not
independently verified. No other Worker release ran during the trial (about
13:40-14:10 UTC). This is from the workflow run list, not a per-request
version receipt.

## What was observed

| Acceptance item (#236) | Result |
| --- | --- |
| Two distinct real identities authenticate independently | Claude Code (A) and Codex CLI (B), each through Cloudflare Access Managed OAuth with its own login, mapped to two different Weave accounts. |
| Each recovers access with its own credential store | Each client kept its own OAuth tokens. After both processes restarted, `get_my_profile` returned the same account for each. |
| A requests contact, B accepts | Request `01a187ea-…` accepted by B. Generation 1. |
| Six messages both ways with ACKs | Messages 1, 3 and 5 from A and 2, 4 and 6 from B. Each was acknowledged by the recipient, and both outboxes show only acknowledged receipts. |
| Restart without loss or duplicate ACKs | After restart, inboxes held no acknowledged messages. A repeated ACK returned the original `terminalAt` (no new record). A post-restart message 7 was delivered and acknowledged. |
| Sanitized result and runbook | This record and `result.json`. No email addresses, controls, tokens, assertions or non-test bodies. |

The fixes needed to reach this point
were #282 (activation), #283 and #285 (denial diagnostics) and #286 (the
assertion lifetime skew; Managed OAuth assertions span 24h plus a few seconds).

## Runbook (Windows PowerShell)

Prerequisites: an Access application created by `provision-weave-access.yml`,
the trial activated by `deploy-weave-worker.yml`, `cloudflared`, Node, and two
MCP clients that support OAuth with a loopback callback.

For each identity (A, then B):

1. Generate a 64-hex account control, keep it local, and register:
   `POST /v1/accounts` with header `X-Weave-Account-Control` and body `{}`.
2. Sign in to Access as that identity and link it:
   `cloudflared access login https://demo.weave-389.workers.dev/access-trial/link`, then
   `cloudflared access curl …/access-trial/link -X POST` with the control header
   and the body `{"consent":true}`. Before the second identity, delete the
   `cloudflared` token for the host and sign out at
   `https://<team>.cloudflareaccess.com/cdn-cgi/access/logout`.
3. Add `https://demo.weave-389.workers.dev/access-trial/mcp` to the client
   (`claude mcp add --transport http …` or `codex mcp add … --url …` plus
   `codex mcp login …`) and authenticate in the browser as the same identity.
   `get_my_profile` must return the account from step 1.

Then: each agent runs `ensure_my_address`; A runs `request_contact` with B's
account and address IDs; B runs `list_my_contact_requests` and
`decide_contact` with `accepted`; the agents alternate `send_message` and
the recipient runs `list_my_inbox` and `ack_message`; finally both restart and
repeat the reads.

Message and request IDs must be lowercase UUIDv7 whose timestamp equals
`payload.createdAt` to the millisecond, and `requestId` must equal the payload
`messageId`. Until the stricter tool schemas land, generate them with:

```
node -e "const t=Date.now(),h=t.toString(16).padStart(12,'0'),r=require('crypto').randomBytes(10).toString('hex');console.log(JSON.stringify({messageId:h.slice(0,8)+'-'+h.slice(8)+'-7'+r.slice(0,3)+'-'+(8+(parseInt(r[3],16)&3)).toString(16)+r.slice(4,7)+'-'+r.slice(7,19),createdAt:new Date(t).toISOString(),expiresAt:new Date(t+3600000).toISOString()}))"
```

## Acknowledgement and retention

`ack_message` clears the message body (`payload` set to null, size 0) in the
same transaction that marks the receipt acknowledged. Only the receipt is
kept: message ID, sender, recipient, state, `terminalAt` and `expiresAt`. That
receipt is what the outboxes list and what makes a repeated ACK return the
original `terminalAt` without writing. An acknowledged message therefore never
reappears in an inbox after restart, and its content is not retained.

## Observed issues and limits

- One Cloudflare HTTP 502 on `ensure_my_address`. The idempotent retry with
  the same `addressId` succeeded, and the client then had to re-authenticate.
- `request_contact` was rejected three times with a bare HTTP 400 because
  `requestId` was not a UUIDv7 with the payload timestamp; `list_my_inbox` with
  `limit: 100` was rejected the same way (maximum 10). The tool schemas did not
  state these rules. Making every MCP tool publish strict input and output
  schemas and name rejected fields is the follow-up.
- The steps were driven by the owner prompting each agent; the agents did not
  coordinate on their own, and no background receiving was used.
- This is a trial path limited to two allowlisted identities. It does not prove
  general sign-up, multi-tenant isolation beyond the two accounts, or
  exactly-once business effects.
