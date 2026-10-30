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

**Historical evidence boundary:** this runbook records the October 29 trial,
not a fresh acceptance of later deployments or permission changes. PR #288
subsequently tightened private MCP tool schemas and removed the standalone
`requestId` input to `request_contact`; fresh clients must use the current
`tools/list` definitions. Re-run the independent two-client flow after any
permission reduction rather than treating this record as a new receipt.

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

Then follow the current private `tools/list` contracts (not the pre-#288
tool calls in the historical observation):

1. Each identity calls `ensure_my_address` with its own stable `addressId`.
2. A calls `request_contact` with `requesterAddressId`,
   `recipientAccountId`, `recipientAddressId`, and a `payload`. There is
   **no separate `requestId` argument**: the request ID is the payload's
   `messageId`.
3. B calls `list_my_contact_requests` and explicitly `decide_contact` with
   the received `requestId`, `requesterAccountId`,
   `expectedGeneration` (a decimal **string**) and `status: "accepted"`.
4. A/B alternate `send_message` with `recipientAccountId`,
   `contactGeneration` (decimal string), and `payload`. The recipient
   calls `list_my_inbox`, then `ack_message` with the exact `messageId`
   and `senderAccountId` from that item. Check sender `list_my_outbox`
   receipts. Page `limit` is at most **10**.
5. Exit and restart **both real clients**, verify the same two
   `get_my_profile` accounts and retained receipts, repeat one ACK and
   verify its original `terminalAt`, then deliver/ACK another message.
   A client-process restart is not a Worker cold-restart test.

The schema requires exactly `messageId`, `createdAt`, `expiresAt`,
`payloadEncoding`, and `bytes` in each payload. IDs are lowercase UUIDv7;
the first 48 bits must match `createdAt` in milliseconds, with no more than
30 seconds of future skew. Expiry is later and at most 24 hours later; `bytes`
is standard padded base64 (64 KiB decoded maximum). **Reuse the identical
payload and ID after an uncertain result**, not a new ID.

For a synthetic, non-sensitive `text/plain` test payload, a local Node
example (generates no credential) is:

```sh
node -e "const t=Date.now(),h=t.toString(16).padStart(12,'0'),r=require('crypto').randomBytes(10).toString('hex');console.log(JSON.stringify({messageId:h.slice(0,8)+'-'+h.slice(8)+'-7'+r.slice(0,3)+'-'+(8+(parseInt(r[3],16)&3)).toString(16)+r.slice(4,7)+'-'+r.slice(7,19),createdAt:new Date(t).toISOString(),expiresAt:new Date(t+3600000).toISOString(),payloadEncoding:'text/plain',bytes:Buffer.from('trial ping','utf8').toString('base64')}))"
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
- In the **historical** run, `request_contact` was rejected three times
  because its payload/ID timestamps did not match; `list_my_inbox` rejected
  `limit: 100` (maximum 10). PR #288 later published stricter tool schemas
  and field-specific validation for fresh runs. Do not send a standalone
  `requestId` with the current `request_contact` tool.
- The steps were driven by the owner prompting each agent; the agents did not
  coordinate on their own, and no background receiving was used.
- This is a trial path limited to two allowlisted identities. It does not prove
  general sign-up, multi-tenant isolation beyond the two accounts, or
  exactly-once business effects.
