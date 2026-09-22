# Trusted operator onboarding on one Host

This completes the existing stage-4 onboarding route, not a new control plane,
identity product, approval database or multi-Host deployment. One existing Host
owns the tools, capability validator and retained invocation/approval journal.

## Choose the surface deliberately

The opt-in `Weave:Operator:Enabled` mode is a **full Host with protected management**.
Every endpoint except the three capability-checked Agent invocation/status routes
and GET `/health` and `/alive` requires `X-Weave-Operator-Key`. This includes existing
workspace, plugin and audit APIs, approval review/decision, OpenAPI and Scalar.
Exemption is internal endpoint metadata, not a URL prefix that another route can
accidentally inherit. Existing global API authentication remains an additional gate.
The operator key does not replace the Agent or reviewer capability.

`AgentOnly=true` remains a different strict mode: management routes are not mapped.
Combining it with operator mode fails startup. Do not start another Host with a
separate SQLite file and describe the two as a shared approval system. The first
onboarding path below uses ONE full protected Host, not a relaxed AgentOnly Host.

This operator key is a deployment-level administrator credential, not a per-human
OIDC identity, MFA scheme or fine-grained administrator-role system. Its holder is
trusted to configure the existing administrative APIs and obtain any declared
credential profile. Agent credentials cannot impersonate it. An operator capable
of issuing both Agent and reviewer profiles is a trusted authority; independent
reviewer subjects do not by themselves establish separation between two humans.

## Deployment configuration

Merge this non-secret example into the existing Host's deployment configuration.
Replace the absolute paths with protected local paths. Keep signing/revocation and
journal storage OUTSIDE the tool root and inaccessible to Agent processes.

```json
{
  "CapabilityTokens": {
    "RevocationDirectory": "/srv/weave/state/revocations",
    "RequireExistingStorage": true
  },
  "Weave": {
    "Invocations": {
      "DatabasePath": "/srv/weave/state/invocations.db",
      "RequireExistingStorage": true,
      "ApprovalRequiredGrants": ["tool:files:invoke:write_file"],
      "Http": { "Enabled": true, "AgentOnly": false, "DecisionsEnabled": true }
    },
    "Operator": {
      "Enabled": true,
      "Tools": {
        "files": {
          "WorkspaceId": "onboarding",
          "Tool": {
            "Name": "files", "Type": "FileSystem",
            "FileSystem": { "Root": "/srv/weave/tools" }
          }
        }
      },
      "Credentials": {
        "writer": {
          "WorkspaceId": "onboarding", "IssuedTo": "agent",
          "Lifetime": "00:05:00",
          "Grants": ["tool:files:invoke:write_file", "invocation:read"]
        },
        "reviewer": {
          "WorkspaceId": "onboarding", "IssuedTo": "operator",
          "Lifetime": "00:05:00",
          "Grants": ["invocation:read", "approval:decide", "tool:files:approve:write_file"]
        }
      }
    }
  }
}
```

Provision `CapabilityTokens__SigningKey` and a DISTINCT `Weave__Operator__Key` through
your existing protected secret/configuration provider. There is no built-in operator
key or public bootstrap endpoint. Use independent, cryptographically random values;
the operator key accepts 32-256 printable non-whitespace ASCII characters. It must
not equal either signing key or `Weave:Auth:Secret`. Protect any configured global
API credential as well. Do not put keys in committed JSON, URLs, process arguments,
request logs or copied diagnostics. Keep the signing key stable during retained
storage recovery; generating a new one on every restart is not recovery.

For the explicit FIRST initialization of an empty deployment, use the existing
storage initialization procedure, with both RequireExistingStorage flags false
only for that deliberate first setup. Then set them true for subsequent boots.
They must never be disabled to conceal lost evidence. See the
[storage recovery limits](2026-09-22-single-host-storage-recovery.md).

Profiles are trusted startup snapshots: 1-64 tool profiles and credential profiles,
bounded route identities and explicit grants, with positive credential lifetime up
to one hour. There is no HTTP body for changing subject, grants, path or tool type.
Changing these deployment profiles requires a controlled restart; issued credentials
remain subject to their existing expiry/revocation rules. Issuing a credential does
not connect a tool or make it available to a Weave-hosted reasoning agent registry.
This path targets the existing external-Agent governed HTTP contract.

## Connect, issue, review and invoke

Start the existing Host with that configuration. Use its configured HTTPS origin.
For local development only, bind to a literal loopback address and the existing
HTTP port, for example `http://127.0.0.1:9401`. Remote operator requests require HTTPS;
`Host`, `X-Forwarded-For` and `X-Forwarded-Proto` are not trusted substitutes. A reverse
proxy must be part of an explicit deployment trust boundary; this change does not
install or configure one. Use end-to-end TLS rather than broadly trusting headers.

Prepare a private `operator.headers` file through your secret tooling containing
`X-Weave-Operator-Key: <your actual operator key>`. If global API auth is enabled,
include its required header there too. File permissions must restrict access to the
operator. Do not share this file or the operator key with the Agent.

The following shell commands assume `WEAVE_URL` is the selected origin and the
private header file exists. They are not an unattended approval workflow:

```bash
umask 077
curl --fail-with-body --silent --show-error --noproxy '*' \
  --header @operator.headers --request POST \
  "$WEAVE_URL/api/operator/tools/files/connect"

curl --fail-with-body --silent --show-error --noproxy '*' \
  --header @operator.headers --request POST \
  "$WEAVE_URL/api/operator/credentials/writer/issue" --output writer.capability

curl --fail-with-body --silent --show-error --noproxy '*' \
  --header @operator.headers --request POST \
  "$WEAVE_URL/api/operator/credentials/reviewer/issue" --output reviewer.capability
```

Do not add `--location`: credentials must not be redirected to another origin.
Connection returns204. Issuance returns200 `text/plain`, containing the existing
base64url capability envelope with `Cache-Control: no-store`. Unknown profiles
return404. Bodies and query parameters on these two endpoints are rejected, not
silently applied. Keep failed HTTP outputs separate from usable credentials; an
output file alone does not establish issuance success.

Deliver ONLY `writer.capability` to the intended Agent over a protected channel.
Its `X-Weave-Capability` header can call the ordinary
`/api/workspaces/onboarding/tools/files/invocations` route; global API authentication
is still needed if enabled. Retain the original invocation ID and complete request.
A write requiring approval returns202 without changing the target file.

The human reviewer uses the EXISTING review helper with the retained original
request, reviewer capability, and the extra operator key:

```bash
# Supply WEAVE_OPERATOR_KEY through the operator's protected environment.
# WEAVE_OPERATOR_BEARER is additionally needed when global bearer auth is enabled.
export WEAVE_REVIEW_CAPABILITY="$(cat reviewer.capability)"
python3 examples/governed-tools/review.py \
  --url "$WEAVE_URL" --workspace onboarding --request retained-request.json
```

The helper prints the full escaped server-verified target/inputs, requires an exact
`approve <digest>` or `reject <digest>` confirmation, and does not execute the tool.
The server checks independent subject, explicit reviewer grants, original input,
live target and plan digest using the existing journal. A changed body cannot reuse
that approval. After approval, the original Agent resubmits the SAME ID and request
with current execution rights. A read-only credential cannot write because a human
approved. Duplicate admitted operations retain the existing non-replay behavior.

For normal Host restart, reconnect the same configured tool through the operator
route and issue fresh narrow credentials deliberately. The new route does not add
auto-dispatch, refresh daemons or persistence for live tool connections. Retain the
existing journal/revocations, including unknown outcomes. Neither an expired token
nor an unconfirmed network response authorizes a fresh-ID retry.

## Verification and remaining boundaries

Real Kestrel acceptance performs connect and issuance exclusively through these
HTTP operations, then pending review, changed-plan denial, approval, current-grant
denial, actual filesystem execution and duplicate-result inspection on the SAME
Host. Additional tests cover missing/wrong/duplicate operator keys, protected old
management/OpenAPI routes, separate global auth, untrusted cleartext/forwarded
headers, request-body injection, invalid startup combinations and AgentOnly absence.
The existing reviewer helper keeps its no-proxy/no-redirect/explicit-confirmation
semantics and only gains the optional operator header.

This is not an OS sandbox, human identity system, network perimeter or guarantee
that arbitrary third-party in-process plugins cannot bypass application controls.
Only the HTTP surface is guarded here; keep Orleans management/gateway ports private.
Disabling operator mode restores prior Host behavior, so do not treat an anonymous
full Host with the flag disabled as a secure deployment. No UI or automatic key
rotation is added. CI/merge evidence is recorded on #129; this guide is not a claim
that a production deployment or real credential initialization was performed.
