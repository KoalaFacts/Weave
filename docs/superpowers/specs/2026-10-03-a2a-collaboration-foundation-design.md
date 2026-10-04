# A2A collaboration foundation design

**Status:** Written design approved for implementation planning on 2026-10-04. See the [implementation plan](../plans/2026-10-04-a2a-collaboration-foundation.md) for the task breakdown and execution-method review. Product implementation has not started.

**Decision:** Add an opt-in A2A boundary for one governed collaboration between two synthetic agent owners. Support the selected HTTP+JSON operations for A2A wire revisions 1.0 and 0.3. Keep authority, approval, invocation admission, and outcomes under Weave's existing controls.

**Baseline:** The design was researched against Weave main `b658a5f33741311ffc4e414f01ab14400cb3c569` on 2026-10-03. Local-execution prerequisites were reconciled with main `b99f1fc7362b3c6f4af5692261441cf04e73f60e` on 2026-10-04 after PR #182 merged. Protocol sources are A2A specification tags `v1.0.1` and `v0.3.0`. This specification describes proposed behavior, not delivered capability.

## 1 Purpose and success

Weave should let assistants belonging to different users cooperate without sharing their owners' unrestricted authority. A2A provides a common language for discovering an agent, requesting work, following its progress, and receiving its output. Weave remains responsible for deciding who may ask, what the receiving agent may do, what its owner must approve, and what evidence establishes the result.

The first milestone proves one complete local path: synthetic owner Alice's agent requests a short note from synthetic owner Bob's agent; Bob's configured acceptance policy admits the request; Bob's separately authorized agent prepares a fixed local document write; Bob's independent approval permits that exact proposal; the original invocation executes at most once through the existing journal; Alice receives the approved note as a task artifact. Rejection, expiration, withdrawal, authority revocation, duplicate messages, restart, and uncertain execution must remain safe.

The fixture uses deterministic agents and synthetic text. It does not contact real personal assistants, use a language-model account, create user accounts, provision production credentials, or establish a production tenant system. Two application principals in one test environment are not two OS security sandboxes.

## 2 Existing behavior and prerequisites

The current source already has exact tool-operation grants, workspace/subject-bound capability tokens, durable invocation and attempt admission, independent approval decisions, frozen stored proposals, owner-scoped queries, and same-UUID resumption. `ToolActor` remains the only path used by this milestone for tool effects. Its SQLite journal remains authoritative about whether an attempt was admitted and its confirmed or unknown outcome.

The current journal deliberately does not own conversation history or a response-body cache. Current AgentRuntime tasks use workspace-bound actors and do not establish external-caller ownership, per-task peer access, or human identity. This design neither exposes those task endpoints as an A2A security boundary nor requires the hosted reasoning runtime.

### Local execution readiness

[PR #182](https://github.com/KoalaFacts/Weave/pull/182) integrated PR #180's local workflow and the three scoped repairs into main at `b99f1fc7362b3c6f4af5692261441cf04e73f60e`: trusted executable/workspace overlap checks, nested preflight-error reporting, and retained null-path validation. [Post-merge CI](https://github.com/KoalaFacts/Weave/actions/runs/37178837375) passed the Linux full solution and Windows CLI boundary tests. PR #180 remains open after the squash; it is not a separate merge prerequisite.

These repairs establish validation and error-handling behavior in the same-user convenience profile. They do not establish OS or cross-user isolation, new macOS runtime acceptance, a new live human/Codex run, or A2A acceptance. Prior live acceptance retains its original revision attribution.

Any acceptance that launches a local agent against a packaged Host must first prove all of the following at the exact tested revision:

- Host executable/bundle, private configuration, governed documents, and each agent-writable directory cannot overlap, including relevant symlink and case aliases
- Invalid, absent, or null persisted paths fail before launch or state mutation
- Direct and wrapped preflight failures are reported as failed operations; diagnostic queries remain distinguishable
- No agent can alter the Host code subsequently run with operator/signing access

The protocol/domain tests may proceed after implementation approval using an isolated test host, ephemeral fixture authority, temporary synthetic directories, and no Codex launch. They must not be labeled packaged-host or OS-isolation acceptance. The landed repair evidence is an input to the exact-revision launch checks, not a substitute for them; A21 remains unexecuted for this A2A milestone.

## 3 Alternatives and choice

1. **Thin opt-in binding with Weave-owned collaboration state — selected.** Reuse the existing authority and invocation machinery. Implement only the chosen HTTP+JSON surface with version-specific source-generated JSON and fixtures. This keeps dependencies and semantics visible; it requires careful protocol tests.
2. **Adopt the official .NET SDK immediately.** It reduces transport plumbing, but the inspected published SDK is `1.0.0-preview2`. Its current main branch is not the same release, its in-memory store is not durable governance, and authentication and tenant scoping remain application responsibilities. A later SDK replacement is possible behind the same boundary after version, error, serialization, persistence, and cookie-isolation verification.
3. **Build a complete federation and workflow platform first.** Portable tenant identity, federation login, public directories, arbitrary delegation chains, streaming, and resource provisioning would postpone a useful governed path. They are outside this increment.

No preview SDK dependency is required by the selected design. This choice does not claim a small binding is universally cheaper or fully protocol-conformant without evidence.

## 4 Feature ownership and composition

- `src/Collaboration/` owns the implemented use cases: SubmitTask, PrepareTask, ResumeTask, GetTask, ListTasks, and CancelTask. Their requests, immutable snapshots, task/message records, and persistence contracts live with this feature. No global Contracts/Core project or empty future feature trees are added
- `src/Authority/` owns the narrow peer-policy decision: validate the authenticated source, receiving route, exact skill, agreement revision, expiry, and revocation. It extends the existing authority vocabulary instead of creating another general authorization engine
- `src/Invocations/` retains approval decisions, frozen tool proposals, attempt admission, execution results, and no-replay enforcement
- `extensions/Weave.A2A/` owns A2A 1.0 and 0.3 HTTP+JSON DTOs, AgentCard projection, bounded client transport, version selection, and protocol/domain translation. Protocol types do not leak into generic feature contracts
- `extensions/Weave.Security.Sqlite/Collaboration/` implements the durable collaboration store alongside the existing SQLite provider. Its tables have a separate feature-owned schema; it does not rewrite invocation records or introduce an in-memory production fallback
- `hosts/Weave.Host/` composes the opt-in adapter, authenticated principal resolver, registered peer policy, persistence, and routes. Endpoints translate transport inputs; they do not decide business permissions
- Tests and `protocol/a2a/` fixtures cover both selected revisions. The existing xunit.v3, Shouldly, and NSubstitute conventions remain in force

Public contracts are small and behavioral: admission, snapshot queries, cancellation, and recipient-authorized preparation/resumption. Store operations use explicit scope and immutable values. The existing Agent actor key format and Orleans field IDs remain unchanged.

## 5 Principals and trust boundaries

### Authenticated identity

The A2A host validates an existing Weave capability in the `X-Weave-Capability` header, then resolves its validated workspace and subject to a configured synthetic peer registration. The matching registered recipient route is also required. The header format is declared as an API-key security scheme in each AgentCard. A missing or invalid capability cannot fall back to anonymous access even if the legacy global API mode is `none`.

The receiving host issues only fixture-local, narrow capabilities through trusted test/bootstrap code. The remote requester receives collaboration send/read/cancel authority for the registered receiving agent; it receives no recipient tool, approval, installation, operator, or signing authority. Recipient work uses a distinct current capability whose subject is the receiving agent. Review uses a third, independent operator subject with existing `approval:decide` authority.

Enabled A2A composition rejects the public development signing key, unavailable authentication, missing durable stores, and ambiguous recipient registrations at startup. No HTTP method introduced here mints capabilities, registers peers, approves a proposal, or provisions long-lived access. Stored tasks contain identity references and authority evidence, never reusable tokens. Fixture secrets are ephemeral and stay outside committed material and agent-writable roots. Production login, token exchange, and durable human ownership need a later design.

### Scoped peer policy

A configured agreement identifies the source owner/agent reference, destination owner/agent reference and workspace, exact skill revision, installation/target revision, allowed input/output classes, expiry, enabled state, and monotonically changing revision. It represents both the source owner's permitted collaboration and the recipient owner's acceptance policy. The fixture explicitly configures both sides; a request or AgentCard cannot create that agreement.

For this increment, ownership references are authenticated configuration facts for the synthetic principals. They are not a migration of all Weave entities to portable Tenant IDs. Names, message roles, route values, AgentCard provider fields, and submitted `tenant` values cannot establish ownership.

Every query or command checks live caller scope before looking up a task. Get and Cancel return the same task-not-found result for absent and inaccessible IDs. List applies scope in the store query before filters, counts, history, artifacts, or pagination. Revoking an agreement denies further peer submission and task reads; the recipient operator can retain local audit access under existing authority.

### Trust in content

All peer messages, descriptions, metadata, errors, and artifact parts are untrusted data. They cannot grant authority, approve work, select another account, change an installation, or become system instructions. The initial skill accepts one structured note request and never executes a command or dereferences a supplied URL.

## 6 One skill and its frozen plan

The only advertised skill is `weave.reviewed-note.v1`. Its application data is `{ "action": "propose", "text": "..." }`. Text is valid UTF-8, nonempty, and at most 16 KiB encoded. Reject every sequence recognized by the existing SecretPlaceholderParser before admission, whether or not that secret is registered. The baseline ToolActor substitutes secret placeholders in all invocation parameters, so literal peer text must not be allowed to select recipient secret material. The preliminary invocation gate also checks this rule for direct ingress, and effective tool input must equal the frozen literal input before dispatch. No path, owner, credential, executable, URL, provider, or tool name is accepted from the caller. The response contains only the approved note and a bounded receipt.

The receiving task allocates opaque task/context IDs and a separate nonempty 32-hex invocation UUID. The output path is server-derived as `<invocation-uuid>.txt` beneath the dedicated receiving documents root. The fixed operation is the registered FileSystem tool's `write_file`. The requesting peer cannot use the generic tool ingress with the recipient's credentials.

The immutable plan includes both registered participant references, skill revision, peer agreement revision, receiving workspace/agent, exact installed target, wire revision, task/context/invocation IDs, and the exact note bytes. Its canonical envelope is included in a server-owned `weave_collaboration_contract` invocation parameter. Existing `InvocationFingerprint` hashes all parameters, and FileSystem normalization preserves them, so this binds the extra collaboration facts into the existing approval digest without replacing that digest or reinterpreting a caller-supplied URL as target authority. The FileSystem target binding still protects the real root/configuration.

The fixed parameter is constructed only from validated stored facts. It is not a general metadata-to-tool forwarding feature. The operator review must show its readable participant/skill/version context alongside the existing frozen path/content review. If that context cannot be rendered and verified, review fails closed. Changed participants, note bytes, protocol revision, agreement revision, or target require a new proposal and approval; they cannot reuse the old plan.

## 7 Task lifecycle and execution

### Submission and preparation

1. The source-side fixture checks its own configured outbound permission and durably records its outgoing receipt before contacting the registered loopback origin
2. The receiving A2A boundary authenticates, validates the chosen wire shape, enforces limits, checks peer policy, and atomically records the normalized request, digest, task/context IDs, invocation UUID, and message receipt
3. For the supported asynchronous configuration it returns the durable task promptly. It does not hold HTTP or a database transaction across the owner decision
4. The independently authorized recipient agent calls PrepareTask with its own current capability. This obtains the frozen task plan, connects the exact configured tool, and invokes the existing governed path under the recipient subject. Required approval produces a durable pending approval and no admitted attempt
5. The recipient operator reviews and approves or rejects through the existing approval API. Peer messages cannot call that API
6. A sender can request continuation with a new message ID, the existing task ID, and application data `{ "action": "resume" }`. That records an idempotent continuation request; it is not an approval or an execution grant
7. The recipient agent calls ResumeTask with current recipient authority. It rechecks peer policy and the complete frozen binding, retrieves the original stored proposal, and uses the same invocation UUID and subject through the existing ToolActor path
8. Only a durably confirmed successful invocation permits the approved note artifact to become available

The deterministic recipient fixture supplies the recipient's capability on each PrepareTask/ResumeTask operation. This milestone does not add an autonomous background credential-renewal service. A message only requests work; the recipient independently decides and authorizes execution.

### States and projection

| Collaboration condition | A2A 1.0 state | A2A 0.3 state | Required behavior |
| --- | --- | --- | --- |
| Durably accepted, awaiting recipient preparation | `TASK_STATE_SUBMITTED` | `submitted` | No claimed tool effect |
| Required independent owner approval pending | `TASK_STATE_AUTH_REQUIRED` | `input-required` | Explain the out-of-band owner review; peer cannot approve |
| Prepared and recipient processing | `TASK_STATE_WORKING` | `working` | No success claim before durable outcome |
| Durable invocation success and artifact available | `TASK_STATE_COMPLETED` | `completed` | Return only authorized artifact content |
| Recipient rejection, expired agreement/approval before dispatch | `TASK_STATE_REJECTED` | `rejected` | Never dispatch; retain evidence |
| Cancellation won before recipient execution claim | `TASK_STATE_CANCELED` | `canceled` | Same task cannot resume |
| Confirmed tool failure | `TASK_STATE_FAILED` | `failed` | Preserve actual recorded error category |
| Invocation outcome unknown, or artifact finalization unrecoverable | `TASK_STATE_FAILED` | `failed` | Include explicit `outcomeUnknown` or `artifactUnavailable` detail and query-only recovery policy; never imply no effect |

The 0.3 pending-approval projection uses `input-required` because its `auth-required` description concerns secondary authentication; 1.0 explicitly describes in-task authorization including human approval. The underlying Weave approval states remain distinct from both wire projections.

A terminal A2A failure is a terminal collaboration result, not evidence that its tool had no effect. Unknown-outcome responses include a plain warning and scoped metadata identifying query-only recovery. New task messages cannot silently restart a terminal task. No transition rewrites the invocation journal's unknown outcome to failure or cancellation.

## 8 Durability and concurrency

The collaboration store persists task scope, immutable plan envelope and digest, bounded accepted note, protocol revision, agreement revision, latest projection, timestamps, message receipts, continuation requests, and artifact receipt. It stores no credentials, private conversation history, or unrestricted tool output. Use additive schema migration and a schema version; preserve all existing invocation/approval tables and files.

Before its first network send, the source-side Collaboration store records a receipt containing stable source/message identity, exact normalized body, selected wire revision, configured recipient/origin and agreement revision, and a digest of those facts. Credentials are excluded. Append returned task/context IDs once learned; they are unknown if the first response is lost. After a lost response or source-process restart, only an explicit retry of the recorded identical submission under the same message ID is permitted for this registered Weave peer with its verified deduplication contract. Do not fabricate a task ID, select another origin/version, or assume arbitrary A2A servers make Send idempotent. Receipt persistence failure prevents network transmission.

A unique key combines stable authenticated source identity and message ID within the receiving authority. Destination identity, installation, and revision belong to the compared immutable digest, not a separate uniqueness partition. An identical duplicate returns the original task without dispatch. Reusing the ID with different normalized content, task/context, recipient, or wire revision fails with a conflict. The wire revision is also part of the digest: switching revisions or recipient installation must not create a second effect under the same source/message identity. Task ID and invocation UUID are different identifiers with a durable one-to-one binding for this skill.

Task persistence must succeed before recipient preparation. The invocation journal must admit the original attempt before the effect. No distributed transaction is claimed between task state, invocation state, and a file write. Safe ordering and recovery are explicit:

- Failure before task commit causes no preparation or dispatch
- Failure after task commit but before pending approval can be recovered by explicit recipient preparation of the same UUID
- A duplicate preparation queries existing invocation/approval records and preserves any retained proposal
- Success in the invocation journal with missing task finalization can reconstruct the deterministic artifact from the frozen approved note and success evidence; it never reruns the write
- Any admitted attempt without a confirmed result remains query-only after restart
- Inconsistent, inaccessible, or unavailable journal evidence fails closed; absence cannot be inferred from a timeout or generic error

CancelTask and the final pre-dispatch collaboration claim share a durable compare-and-set on task state. If cancellation wins, connector dispatch is blocked even if an invocation attempt was just admitted; a no-effect denied/canceled attempt may therefore exist. If the dispatch claim wins, CancelTask returns TaskNotCancelable rather than promising to stop an effect. A crash after that claim does not free permission to redispatch; restart queries the original invocation and approval. Confirmed pre-admission absence permits an explicit same-UUID recovery under current authority; uncertainty does not.

Revalidate the agreement and recipient grants at the existing final pre-dispatch boundary, after invocation-journal admission. A single-host coordinator serializes agreement disable/revocation and the collaboration dispatch claim. That claim is the linearization point for accepting execution: a peer-policy revocation ordered before it prevents dispatch; a revocation ordered after it cannot promise to stop the accepted operation. Canceling or revoking permission never undoes an effect. This boundary does not claim cross-node fencing, instantaneous remote revocation, or distributed exactly-once execution.

### One dispatch gate for every ingress

The frozen envelope is integrity evidence, not enforcement by itself. Add a narrow invocation dispatch-gate contract owned by Invocations and implemented by Collaboration. ToolActor/InvocationExecution must call it for collaboration-bound requests through every ingress, including direct actor invocation, generic HTTP invoke/resume, and A2A recipient preparation/resumption.

The preliminary check runs for every normalized invocation and resolves an authoritative binding by receiving workspace and invocation UUID, independently of the marker's presence; it then verifies the original recipient subject, exact tool/operation, and complete input/target binding; it validates the server-frozen envelope and denies canceled/terminal/inconsistent bindings. The final gate runs only after a new journal attempt has been admitted and after current recipient capability and target revalidation, immediately before connector dispatch. It requires a recipient continuation permit previously recorded by ResumeTask after current peer-policy checks, then atomically revalidates that policy and acquires the task's dispatch claim for that exact attempt. Generic invoke/resume cannot create the continuation permit, and approval alone does not create it. Pending approval does not acquire a dispatch claim. After the gate's durable I/O returns, repeat recipient capability and grant validity, token expiry, approval validity and expiry, cancellation, and target revalidation before connector dispatch; gate latency must not create stale authority. The recipient permit is bound to the frozen plan and is not stored credential material. Failure to resolve or durably claim prevents the connector call and is recorded through the existing no-dispatch outcome path.

A duplicate attempt never reacquires a dispatch permit. The existing journal replay rules continue to return metadata rather than execute. If recording the blocked outcome fails, preserve the journal uncertainty; do not invent a successful cancellation of an already dispatched effect.

The reserved collaboration envelope requires its gate. Missing registration, unavailable task storage, unknown binding, altered envelope, or disabled collaboration fails closed for that request. Disabling A2A routes must not disable protection of retained collaboration proposals. Ordinary invocations without a collaboration binding retain their existing behavior; a caller cannot strip the envelope from a bound proposal because the frozen invocation fingerprint would no longer match.

No safety-critical gate uses last-registration-wins or an optional allow-all fallback. This is one concrete precondition in the existing executor, not a second approval journal or independent executor. A recipient's unrelated action with a fresh UUID remains subject to its own grants; it cannot be reported as continuation of the canceled task.

## 9 Exact protocol surface

### Version and transport

Pin the source specifications to `v1.0.1` and `v0.3.0`. Use wire major/minor `1.0` for 1.0 requests and cards; the 0.3 AgentCard retains its specified `protocolVersion: "0.3.0"`. This is the intentional older card schema, not 1.0 negotiation behavior.

Expose HTTP+JSON only. For each explicitly configured synthetic recipient, the host supplies two version-specific card URLs and two version-specific base URLs. Clients obtain those URLs from trusted fixture configuration; they do not search a public directory. The 1.0 card uses `supportedInterfaces` with `protocolBinding: "HTTP+JSON"` and `protocolVersion: "1.0"`. The 0.3 card uses `url`, `preferredTransport: "HTTP+JSON"`, and its 0.3 fields. Do not advertise a JSON-RPC or gRPC interface that is absent.

The 1.0 interface requires `A2A-Version: 1.0`. A missing version is interpreted as 0.3 and rejected there with VersionNotSupported. The dedicated 0.3 endpoint accepts an absent header or `0.3`; conflicting/unsupported versions fail before task admission. The client selects a configured supported revision explicitly; it never retries a send under another revision after failure or response loss.

### Operations relative to the selected base URL

| Operation | A2A 1.0 | A2A 0.3 |
| --- | --- | --- |
| Send | `POST /message:send` | `POST /v1/message:send` |
| Get | `GET /tasks/{id}` | `GET /v1/tasks/{id}` |
| List | `GET /tasks` | `GET /v1/tasks` |
| Cancel | `POST /tasks/{id}:cancel` | `POST /v1/tasks/{id}:cancel` |

This is an explicitly asynchronous profile. A 1.0 Send requires `configuration.returnImmediately: true`; an omitted/default-false or explicit false value is rejected as an unsupported blocking operation before task admission. A 0.3 Send accepts absent/false `configuration.blocking` and rejects true under the specification's long-running-task allowance. The card skill description documents this restriction and the fixture client sets it explicitly. This bounded operation profile is not a claim of complete support for every Send configuration.

Send returns the version-correct `{ "task": ... }` wrapper. Get and Cancel return a Task. The 0.3 Cancel body carries `name: "tasks/<id>"` and must match the path. The 1.0 cancel body/path are interpreted under its tagged request model; conflicting identity values are rejected, never substituted.

Use the tagged Markdown/TypeScript HTTP+JSON data model for 0.3 and the tagged proto-derived JSON model for 1.0. In particular, 0.3 messages/tasks/parts use their `kind` discriminators and lowercase role/state names; 1.0 uses named union fields, `ROLE_USER`/`ROLE_AGENT`, and `TASK_STATE_*`. Do not serialize the older gRPC-only `content` layout as the documented 0.3 REST `parts` layout. Version-specific golden fixtures must catch this distinction.

A 0.3 List response is the documented array of scoped tasks. A 1.0 List response includes `tasks`, `nextPageToken`, `pageSize`, and `totalSize`, with its documented filters and cursor pagination. `totalSize` is computed only within authorized scope. A cursor is opaque and server-bound to principal, recipient, protocol revision, filters, and snapshot position; altered or foreign cursors fail. Sort by status update time descending with task ID as a stable tie-breaker. The 1.0 final cursor is the empty string. When `includeArtifacts` is false, omit artifacts entirely. History length zero omits history; returned history contains only bounded peer messages.

### Capabilities and unsupported features

Both cards advertise one skill, `application/json` input, and `text/plain`/`application/json` output. The local demo card states that it is a synthetic local profile. Streaming and push notifications are false; authenticated extended cards are not advertised. AgentCard retrieval is read-only and reveals no private task, root, token, or owner personal details. Public card bytes describe the synthetic service; task operations still require authentication.

File/url/raw parts, callbacks, push configuration, SSE subscriptions, gRPC, JSON-RPC, arbitrary new contexts, referenced foreign tasks, and unrecognized required extensions are unsupported. Return the corresponding version-specific protocol error. Required features cannot be silently ignored. New tasks omit task/context IDs; continuation refers only to the original authorized task, and any supplied context must match it.

Use `application/a2a+json` for 1.0 and `application/json` for 0.3. Parse supported compatible JSON content types deliberately rather than depending on a framework default. The 1.0 error envelope follows its HTTP binding, including `google.rpc.ErrorInfo` with the exact A2A reason. The 0.3 REST error envelope consistently maps its defined A2A errors and HTTP statuses. Authentication failures remain HTTP 401; forbidden admission is 403; absent/inaccessible tasks are 404. TaskNotCancelable is a protocol error, not HTTP success or a canceled task.

## 10 Limits and local network safety

- Request body: 64 KiB; note text: 16 KiB UTF-8; JSON depth: 16; exactly one supported application data part per skill message
- Task-owned transcript: initiating and continuation messages only, at most 16 retained messages per task; excess continuation requests are rejected before mutation
- Artifact total: 24 KiB per task; at most two artifacts, one approved note and one receipt; never return a local path or raw exception
- Per registered peer/recipient profile: at most 100 retained tasks and 8 active tasks. At capacity, reject new tasks with explicit capacity evidence; do not delete old evidence silently
- The 0.3 unpaginated list is bounded by that 100-task profile quota. The 1.0 default page is 50 and accepted page size is 1 through 100
- Loopback connection and request deadlines: 5 and 30 seconds respectively. No automatic send retry. Poll interval: at least one second with a caller-cancelable local fixture deadline; timeout is an observation failure, not task cancellation

The initial profile accepts only configured literal loopback origins and disabled redirects, proxies, and cookies. A malicious card cannot redirect credentials to another origin. Arbitrary discovery URLs, DNS rebinding, private-network discovery, artifact URL fetching, and cross-origin credential forwarding are not implemented. Remote production operation requires a later explicit HTTPS and identity design; loopback does not establish production transport or OS isolation.

## 11 Acceptance tests

Every behavioral test runs for both wire revisions unless the row names a version-specific shape. Protocol tests exercise real loopback HTTP and SQLite files, not only handler mocks. Independently inspect file bytes and invocation/approval state for effect assertions.

| ID | Scenario | Required evidence |
| --- | --- | --- |
| A01 | Approved note | Two distinct configured owners/agents; pending proposal has zero attempts and no target; independent approval; same UUID resumes; one successful attempt; exact approved UTF-8 content; authorized artifact |
| A02 | Rejected or expired review | No target and no attempt; task becomes rejected; later messages cannot resurrect it |
| A03 | Peer self-approval | `approve` text, metadata, forged role, or approval fields cannot change approval; requester has no decision/tool authority |
| A04 | Unknown, missing, expired, revoked, wrong-workspace capability | Proper denial and zero task/effect admission; no anonymous fallback |
| A05 | Foreign task/context/tenant/agent IDs | Same inaccessible/not-found behavior; no returned history, artifact, existence count, or cursor leakage |
| A06 | Scope of List | At least three owners in fixture; unfiltered and filtered lists/counters/cursors include only authorized peer tasks |
| A07 | Duplicate message | Concurrent identical sends yield one task/UUID; changed body, recipient, context, or protocol revision conflicts; no second effect |
| A08 | Concurrent resume | Many resume requests and two recipient workers still produce one journal attempt and one file effect |
| A09 | Current authority changes | Revoke sender agreement before the execution claim, or recipient grant/target at the existing final revalidation boundary; deny with no effect. Block gate persistence deliberately and expire/revoke recipient authority or expire approval during the wait; post-gate revalidation must still prevent dispatch. Peer-policy revocation ordered after the claim explicitly cannot promise to stop the accepted operation |
| A10 | Changed plan or installation | Text, path-derived target, peer agreement revision, tool configuration, or wire revision change invalidates the frozen approval |
| A11 | Persistence failure | Failed task admission causes no tool call; failed invocation admission causes no effect; finalization loss never repeats a successful effect |
| A12 | Restart at each boundary | Before proposal, pending approval, after decision, after claim, after effect/lost response, and after recorded success: preserve IDs, decisions, and no-replay rules |
| A13 | Cancel race | Controlled synchronization demonstrates cancel-wins means no dispatch; execution-claim-wins returns not-cancelable; unknown effects never become canceled |
| A14 | Lost HTTP response | Source receipt is durable before send; restart the source after receiver acceptance and before the response. Task/context IDs remain unknown until identical resubmission retrieves the original task; no fallback version, target, or message/UUID identity |
| A15 | Unknown invocation outcome | Inject effect followed by response loss; task says query-only/unknown; retained attempt remains unknown after restart; zero redispatch |
| A16 | Wire fixtures | Correct cards, routes, headers, media types, unions, roles/states, send wrapper, get/cancel shape, and exact protocol error reasons. Explicit 1.0 async succeeds; default/false blocking fails before admission; 0.3 blocking true fails |
| A17 | List version differences | 0.3 array; 1.0 complete paginated shape, final empty cursor, stable order, correct history omission and artifact omission |
| A18 | Unsupported or hostile content | Oversized/deep/invalid/null input, URL/file parts, callback endpoints, foreign reference tasks, unknown required extension: no state mutation or outbound fetch |
| A19 | Origin and credential isolation | Card endpoint substitution, redirect, cookie, proxy, and non-loopback endpoint cannot receive fixture credentials; independent peer requests do not share authentication state |
| A20 | Approval context review | Readable participants/skill/version match server-frozen evidence; changing only collaboration context changes approval digest; peer preview is ignored |
| A21 | Local launch prerequisite | Host bundle/private/documents/agent roots separated including aliases; null paths rejected; nested preflight failures are errors at exact launch-tested revision |
| A22 | Disabled composition | Default Host has no A2A task routes; missing authority/store or ambiguous peer/recipient mapping fails startup when enabled |
| A23 | Quota and cancellation limits | Capacity and deadline failures are explicit; evidence is retained; caller disconnect does not erase accepted task or grant replay |
| A24 | Generic ingress bypass | Before a valid recipient continuation permit, after cancellation, or after pre-claim peer revocation, directly invoke and call generic HTTP resume with the unchanged valid recipient capability and approved UUID; no connector effect. Wrong subject with a stripped marker before PrepareTask, altered marker, missing/disabled gate, or failed task-store read also denies bound invocations |
| A25 | Literal input and secret substitution | Register a synthetic secret canary and submit its placeholder, including through direct ingress; reject before proposal/effect/disclosure. Ordinary literal Unicode round-trips unchanged; any effective-input mismatch prevents dispatch |

For implementation verification, run the repository's Python checks, normal and locked restores, Release build, full solution tests, relevant focused tests, formatting, check-rules, and adversarial review. Inspect skips and report exact tested commits. Non-.NET interoperability must include a standard-library client that sends the pinned fixtures and independently verifies response shapes; an external network peer is unnecessary. Full independent SDK interoperability is a separate claim and is not established by shared DTO round trips.

## 12 Non goals and future boundary

This milestone does not deliver production multi-tenancy, human login/MFA, public agent federation, portable agent-key migration, real external communication, remote deployment, new long-lived credentials, arbitrary workflows, automatic recovery of unknown effects, distributed exactly-once execution, a public directory, marketplace, streaming, webhooks, file transfer, or recursive delegation.

Future external interoperability can reuse this protocol boundary, but each additional skill, identity provider, remote installation, credential path, output class, or transport needs its own policy and verification. Consent to one note exchange is not unrestricted permission for future requests or disclosure of private memory.

## 13 Review and delivery gate

This change is documentation-only. The specification must pass a placeholder/ambiguity scan, source-path and protocol-reference checks, and a consistency review of authority, state, cancellation, and recovery. It must be presented for user review before an implementation plan is written. A later approved plan must identify exact source changes, test order, prerequisites, and execution method.

No successful code build, protocol conformance run, local-agent acceptance, fix to #180, merge, or deployment is asserted by this document.

## References

- [Weave architecture at the inspected baseline](https://github.com/KoalaFacts/Weave/blob/b658a5f33741311ffc4e414f01ab14400cb3c569/ARCHITECTURE.md)
- [Weave contributor instructions](https://github.com/KoalaFacts/Weave/blob/b658a5f33741311ffc4e414f01ab14400cb3c569/AGENTS.md)
- [Invocation execution and replay rules](https://github.com/KoalaFacts/Weave/blob/b658a5f33741311ffc4e414f01ab14400cb3c569/src/Invocations/InvokeTool/InvocationExecution.cs)
- [Invocation input fingerprint](https://github.com/KoalaFacts/Weave/blob/b658a5f33741311ffc4e414f01ab14400cb3c569/src/Invocations/InvokeTool/InvocationFingerprint.cs)
- [Reviewed approval boundary](https://github.com/KoalaFacts/Weave/blob/b658a5f33741311ffc4e414f01ab14400cb3c569/src/Invocations/DecideApproval/ToolActor.DecideReviewedApproval.cs)
- [Stored proposal resume endpoint](https://github.com/KoalaFacts/Weave/blob/b658a5f33741311ffc4e414f01ab14400cb3c569/hosts/Weave.Host/Invocations/Resume/ResumeInvocationEndpoint.cs)
- [PR 180](https://github.com/KoalaFacts/Weave/pull/180), including [Host bundle isolation finding](https://github.com/KoalaFacts/Weave/pull/180#discussion_r4174971861), [preflight error finding](https://github.com/KoalaFacts/Weave/pull/180#discussion_r4174971859), and [null path finding](https://github.com/KoalaFacts/Weave/pull/180#discussion_r4174971864)
- [A2A v1.0.1 release](https://github.com/a2aproject/A2A/releases/tag/v1.0.1)
- [A2A v1.0.1 specification](https://github.com/a2aproject/A2A/blob/v1.0.1/docs/specification.md)
- [A2A v1.0.1 authoritative protocol model](https://github.com/a2aproject/A2A/blob/v1.0.1/specification/a2a.proto)
- [A2A v0.3.0 specification](https://github.com/a2aproject/A2A/blob/v0.3.0/docs/specification.md)
- [A2A v0.3.0 HTTP JSON types](https://github.com/a2aproject/A2A/blob/v0.3.0/types/src/types.ts)
- [Official A2A .NET package 1.0.0-preview2](https://www.nuget.org/packages/A2A/1.0.0-preview2)
