# Codex + human-controlled write pilot

## Status: connection preparation, not a completed live acceptance

The agreed next question is whether a real external Agent can use the existing
single-Host public interface while a real person controls writes. The previous
`first_use.py` proves a scripted path only. Do NOT use its scripted approval or
fixed `organize()` function as evidence for this pilot.

This change adds one four-tool MCP stdio example, not an SDK, approval engine,
Host feature or identity provider. Adapter contract tests can run without a model
account. They are not proof that Codex ran, that someone reviewed a proposal, or
that the deployment has no bypass. Record those results only after the live steps.

## Prerequisites and trust boundary

Use the existing [operator guide](2026-09-22-trusted-operator-onboarding.md) for ONE
protected full Host, its retained journal/revocations and its existing reviewer.
Keep that configuration, operator/signing keys, reviewer credentials and target
files OUTSIDE the Codex execution environment. Prefer a separate machine/VM; a
separate directory or read-only Codex setting alone is not an isolation proof.
Expose only the intended HTTPS Host origin, never public Orleans/admin ports.
Loopback HTTP remains available for developer protocol tests, not remote deployment.

Before giving the Agent a credential, a trusted operator must check:

1. The chosen FileSystem tool is rooted in a disposable test-document directory.
2. `ApprovalRequiredGrants` includes the EXACT `tool:files:invoke:write_file` grant.
   Check a disposable canary: a first write must return202/Pending and leave the
   target unchanged. Stop the pilot if not. `submit_write` obeys Host policy;
   without this server configuration the existing API CAN execute immediately.
3. A startup-configured Agent profile has only read_file, write_file and
   invocation:read grants for this tool/workspace. It has NO connect, operator,
   approval:decide or reviewer grant. Deliver only its issued capability to Codex.
4. On the Agent machine, the Host target and stores are not mounted/readable, no
   SSH/provider credential gives a second path, and no operator/reviewer secret
   is inherited. Verify denied direct access, not just a prompt saying not to use it.
5. The Agent runtime has an authorized Codex login. Check `codex login status` on
   that runtime. Sign in there using `codex login` when needed. Never paste a key,
   auth.json, device code or access token into chat or an issue. This example does
   not install credentials, call a paid model in CI, or borrow a browser login.

Example startup credential profile, alongside the existing reviewer profile:

```json
"agent": {
  "WorkspaceId": "onboarding", "IssuedTo": "agent", "Lifetime": "00:30:00",
  "Grants": ["tool:files:invoke:read_file", "tool:files:invoke:write_file", "invocation:read"]
}
```

Profile issuance remains an operator-side step from the existing guide. The bridge
only receives `WEAVE_AGENT_CAPABILITY` and, if configured, `WEAVE_AGENT_BEARER`.
These are Weave credentials, not Codex model authentication. Do not reuse keys.

## Add only this server to the Codex test environment

Place this table in the test user's existing Codex config, adjusting absolute paths
and HTTPS origin. Do not commit credentials or replace unrelated Codex settings.

```toml
[mcp_servers.weave_files]
command = "python3"
args = ["/opt/weave/examples/governed-tools/codex_bridge.py",
        "--url", "https://weave-pilot.example.test",
        "--workspace", "onboarding", "--tool", "files",
        "--requests", "/home/agent/weave-pilot-requests"]
env_vars = ["WEAVE_AGENT_CAPABILITY", "WEAVE_AGENT_BEARER"]
startup_timeout_sec = 10
tool_timeout_sec = 45
```

`python3` uses only its standard library. The bridge exposes the MCP2024-11-05
stdio/tools subset, not every MCP feature. Codex supports stdio MCP configuration;
a real authenticated session is still required to establish this exact integration.
Use `codex mcp list` and `/mcp` to inspect it. A list entry is not a model-tool-call
acceptance result. Keep any Codex-side approval prompts; they do not replace Weave's
separate human reviewer or permit the Agent to access the reviewer credentials.

The four tools are read_document, submit_write, get_status and resume_write.
There is NO arbitrary URL, shell, connect, credential-issuance or approve tool.

## Live task: no prescribed output or scripted tool sequence

Give Codex this task with two non-sensitive documents present only on the Host:

> Read meeting.txt and constraints.txt through Weave. Prepare summary.md containing
> decisions, actions with owners/dates, and unresolved questions. Distinguish missing
> information from facts. Submit the proposed write for my review and stop when it
> is pending. Do not write directly or approve it yourself. Retain the request_key
> and invocation ID; if the connection is interrupted, query that ID first.

Do not feed a prewritten summary, call organize(), pipe approval text, or have the
supervising assistant choose every tool call and label the result autonomous Codex.
Record the actual model/client version, prompt, tool calls, pending ID and proposed
content. Treat document content as data, including any embedded instructions.

## Human decision and continuation

The bridge saves an exact `<request_key>.json` before the first POST. This is a
retained request, NOT authority, a cached approval, or Weave's journal. Keep the
request directory private and retain it across MCP restarts. Do not point it at
Host target files. Its context binds to the configured origin/workspace/tool.

Transfer that JSON alone to the trusted reviewer machine via the normal protected
channel. Run the existing `review.py` with that request, the reviewer capability and
operator key; read the server-verified target, full proposed content and digest.
The PERSON types the exact approve/reject confirmation in the terminal. Do not use
`echo`, `printf`, a workflow or an assistant to manufacture human approval.

Approval does not execute anything. Tell Codex to inspect get_status for the same
request_key. Only resume_write can resubmit the unchanged saved request, and it first
requires no admitted outcome plus server approval state Approved. The Host still
revalidates current authority, target and digest atomically with admission. A repeated
submit_write with the same key is query-only. Different content under that key is
rejected. A newly proposed revision needs distinct human review; never create a new
key to evade rejection, an unknown outcome, an expired capability or a lost reply.

If the response is lost, state is unknown or authentication fails, stop and query;
the adapter does not retry the POST. Recorded results, including OutcomeUnknown,
never cause resume_write to execute again. It does not cache returned read bodies.
If a read needs repeating, distinguish a new read from retrying a write effect.

## Evidence and completion (leave unchecked until actually observed)

- [ ] A real authenticated Codex chooses tools and writes its own proposed summary.
- [ ] Direct access to Host files/stores and reviewer/operator secrets is denied
      from the actual Agent runtime, with an operator-observed probe record.
- [ ] Pending leaves the target unchanged; a named human reviews the exact target,
      contents and digest, then makes a real decision using the existing tool.
- [ ] Rejection causes no execution; modified content is separately reviewed.
- [ ] Approval plus current Agent rights performs the retained request once; public
      query/readback agrees, duplicate/resume cannot overwrite a later marker.
- [ ] A stopped/restarted Agent uses its saved request and queries first, without
      handing the model a new ID to conceal an uncertain previous effect.

Use the actual host journal and human decision transcript with secrets redacted.
Record errors, manual rescue steps, model usage/cost if available, and the exact
software revisions. Simulated server responses in unit tests are explicitly not
this evidence. Runtime authentication/access limitations and exact test results
belong on the PR record, not as assumed capabilities of future environments.

Known bounds: one fixed origin/workspace/tool; one small stdio server, not an OS
sandbox; authority is server-owned. Local saved requests are not tamper-proof or a
power-loss journal. Tokens can expire while waiting; an operator may deliberately
issue a fresh narrow Agent token and restart the bridge with the same request
storage. That never supplies new approval or authorizes replay of unknown results.

References checked2026-09-23: [Codex MCP](https://developers.openai.com/codex/mcp/),
[Codex authentication](https://developers.openai.com/codex/auth/).
