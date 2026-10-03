# Local Codex first use through the published CLI

## Scope

The local profile completes the existing filesystem governed path for an external
Codex. `weave local init`, `serve`, `codex`, `review` and `status` own operator
onboarding and invocation ingress in the CLI. They reuse the ordinary Host's
protected operator endpoints, independent reviewer checks and UUID proposal APIs.
They do not add another approval database or reasoning loop.

The business MCP endpoint is native CLI stdio with the same four operations:
read_document, submit_write, get_status and resume_write. It has no management,
issuance, approval or arbitrary HTTP tools. Local receipts freeze UUID/hash before
initial submission; losing a response does not permit an automatic POST retry.
Resumption loads the original body on the server and uses current authority.

## Preserved, changed and removed

- Preserved: exact operation grants, required write approval, original subjects,
  frozen server proposals, atomic attempt admission, unknown outcomes, retained
  IDs, ordinary Codex sandbox/tool review, zero-argument interactive Weave, global
  CLI configuration and the existing 94xx/storage namespace defaults.
- Changed: the explicit new local profile generates protected deployment files
  and bounded credentials without manual secret handling. Native Host executables
  launch directly; DLL and project development paths still use `dotnet`. Binary
  release archives include a sibling self-contained `host/` directory.
- Replaced for this onboarding route: manual Python/Node bridge and reviewer setup.
  Existing examples remain diagnostic entry points and historical evidence; no
  compatibility shim or duplicate server authority is introduced.

## Initialization and boundary

Only a nonexistent dedicated private directory can initialize with absent stores.
The ordinary Host initializes both stores before the CLI marks the deployment
ready. Before recording that marker, it changes both RequireExistingStorage flags
to true. All subsequent local serving requires them. Interrupted initialization
is retained and fails closed; initialization never overwrites existing state.

This is a deliberately local, same-user profile: literal loopback HTTP, optional
global auth explicitly none for new deployments, capability-protected business
routes and operator-protected management/review. Configuration, state and Agent
working directories cannot overlap the selected document root. The private root
uses restrictive Windows ACLs or Unix 0700. It is not an OS sandbox and does not
change existing installations or remote transport rules.

Human review requires a real interactive terminal and exact digest confirmation;
there is no scripted approval option. Content display escapes terminal controls
and directional formatting. A reviewer decision never resumes the tool. Status
and fresh Agent sessions keep the original UUID and subject, including after
bounded credential renewal.

## Verification ownership

CLI regressions cover the failing native-executable launch, private initialization
and retained-state guards, bounded transport body cancellation, four-tool discovery,
wrong/inexact/noninteractive review, original-ID resume and lost-response receipts.
Scripted terminal fakes are protocol tests, not proof that a person approved.
Real published-bundle and fresh-Codex observations must be recorded separately.
Published acceptance found that ignoring Codex user configuration did not prevent
installed plugin MCP companions from loading. The launcher additionally disables
plugins in this one session; a failing regression covers the missing flag, normal
sandbox approval and the four-tool configuration. Original pending proposals are
preserved across this launcher correction.
The earlier live approval at the main baseline establishes the existing server
path; it is not proof of every new CLI entry point.

See [the user guide](../local-codex.md) for source-checked commands. Cross-platform
packaging is defined by the workflow; runtime acceptance applies only to platforms
actually exercised. A local build is not a release, public upload or independent
review.

### Repository checks

- Python standard-library checks: 111 total, zero failures, one POSIX-shell check
  skipped because its executable was unavailable on this Windows host.
- Ordinary and locked solution restore passed. Release solution build passed with
  zero warnings and errors.
- Full Release suite: 3,223 succeeded, zero failures, 14 skipped for unavailable
  Docker, Windows symlink privileges or the external Echo endpoint.
- After the final private-directory ignore rule, Release CLI build and all 231
  CLI tests passed without warnings, failures or skips.
- After the observed plugin-loading correction, all 232 CLI tests passed. The
  missing session flag was first reproduced by a failing regression.
- Scoped check-rules and adversarial self-review covered initialization, retained
  state, process arguments/environment, transport bounds, lost responses, human
  decision input, meaningful regressions and documentation. No remaining findings;
  no independent reviewer or cross-platform runtime acceptance is claimed.

### Published Windows acceptance

The self-contained CLI and Host initialized a new sibling-folder deployment with
`dotnet` absent from the child PATH and DOTNET_ROOT pointing to a nonexistent
directory. The bundle contains .NET and ASP.NET Core 10.0.12. Initialization
created both retained stores, required them on subsequent boots and protected the
private directory; repeat initialization refused without changing configuration.

Real signed-in Codex CLI 0.156.1 read both seeded Chinese documents through native
Weave MCP, generated its own summary and submitted UUID
`9d8dbf376e524b8192aa7f6c143ec025`. Server status was Pending with no invocation
record, and the original summary sentinel bytes were unchanged. These observations
prove native first use and proposal admission; they do not claim that a person has
yet approved this new CLI proposal. Human decision and execution need their own
retained evidence.

### Human decision continuation repair

The distinct live `next-steps.md` request received a real human approval on
October 1, but no Agent continuation ran before expiry. The approved record and
zero-attempt history are preserved; that expired request must not be resumed or
replaced to bypass expiry.

`local review --continue` now coordinates immediate continuation after a confirmed
interactive approval. Review itself only records the decision. A separate workflow
queries the original UUID with fresh Agent authority and starts a fresh Codex under
the same configured subject only when Approved without an execution record. The
Agent must query first and resume the server-retained original. Rejection, inexact
input, unconfirmed decisions, expiry, denied access and existing outcomes do not
start the Agent. Its exit code is followed by an independent outcome query; only
a completed successful attempt confirms execution. Failure or cancellation never
triggers an automatic retry or replacement UUID.

Each Codex launch reconnects the configured document tool through the protected
operator API before issuing its narrow credential. This addresses the observed
tool disconnection after idle actor collection, including a wait for human review.
Operator/reviewer credentials remain outside the Agent process.

The missing continuation was first reproduced by a failing regression: a confirmed
approval started zero Agents. CLI protocol tests now cover continuation, rejection,
changed approval state, denied queries, existing/unknown outcomes, unsuccessful
process exit, cancellation and unconfirmed final execution. These terminal fakes
do not establish a new real human approval or a real external write.

The isolated branch also incorporates main's existing routing-readiness and
dedicated Python pipe-reader fixes. Before that sync, the default parallel full
run had six failures in MCP readiness, Host recovery, manual review and cancellation
tests. After rebuilding with the main fixes, the full suite with
`--max-parallel-test-modules 1` passed: 3,263 succeeded, zero failures and five skips
(Windows symlink privileges and the external Echo installation endpoint).
The Release solution build had zero warnings/errors, all 258 CLI tests passed,
and Python checks had 111 total with one unavailable POSIX-shell skip. This is a
passing serial-module gate; the post-sync default parallel suite was not rerun.
Scoped repository checklist review was self-review with no remaining findings.

### Native UTF-8 boundary

The independent live `review-checklist.md` proposal reached Pending, but comparing
the server's complete original with the Codex tool arguments revealed mojibake.
Windows `Console.In` used the console code page for UTF-8 MCP bytes. No human
review was opened and no decision or write occurred. That frozen proposal remains
unchanged; a different task requires explicit authorization.

The MCP entry point now opens the standard byte streams directly and the server
wraps them with strict UTF-8 readers/writers, without a BOM or console-code-page
dependency. Invalid UTF-8 stops before HTTP or a receipt. Human review output also
selects UTF-8 when first used, so Chinese text does not depend on the caller's
output code page. These changes do not rewrite saved proposals or approval digests.

A real CLI subprocess receives raw UTF-8 JSON through stdin and sends the proposal
to a loopback TCP fixture. Before the fix, the Chinese target filename was
corrupted; after the fix, both filename and multiline Chinese/emoji content must
match exactly. The fixture also checks Pending rather than execution and output
without a BOM. A denial regression rejects invalid UTF-8 before any HTTP request.
All 260 CLI tests passed after the MCP repair. The TCP fixture proves the native
encoding boundary, not real human approval or a real filesystem write.

The final Release solution build had zero warnings/errors. A full serial-module
rerun passed 3,265 tests with zero failures and five environment skips. The earlier
interrupted run recorded two Host service tests failing after unusually long
elapsed times; all six related cases passed in a focused recheck. The rerun does
not establish the cause of those delays or default parallel stability.
Locked restore and both local self-contained publishes passed. A published CLI
smoke check without dotnet on PATH preserved a raw Chinese/emoji JSON-RPC ID,
emitted no BOM, rejected invalid UTF-8 and created no receipts. That protocol-only
check sent no business HTTP request, human decision or filesystem write.
Scoped check-rules and adversarial review were self-review with no findings;
package/provider, persisted schema and architecture changes were not applicable.

### Human waiting window repair

The independent Chinese proposal reached Pending with its exact UTF-8 body, but
the human entered the confirmation after its 30-minute proposal deadline. The
retained journal contains no decision, invocation or attempt for that UUID, and
the target file remains absent. The terminal wrapper also failed while processing
its transcript in Windows PowerShell 5. This run did not complete human approval
or execution.

New local configurations now explicitly allow 24 hours for proposal approval.
Agent and reviewer capabilities retain their 30-minute lifetime. After exact
interactive confirmation, review obtains a fresh reviewer capability before
submitting the original digest and decision. Issuance failure or cancellation
does not send a decision. The server still validates the original frozen deadline
and current authority. Existing configuration requires an explicit local setting
update; retained proposals are never renewed, rewritten or assigned another UUID.

The regressions first reproduced approval/rejection failing after a 31-minute
human wait and the missing local approval lifetime. Tests cover renewed authority
after exact confirmation, denied issuance and cancellation, alongside existing
inexact-input and continuation stop paths. These protocol fixtures do not prove
a new human decision or real filesystem execution. Operators can run the native
review command directly in a current terminal without transcript wrappers.

Verification: the Release solution build had zero warnings/errors; normal and
locked restores passed. Python checks ran 111 tests with one POSIX-shell skip.
All 266 CLI tests passed. The full serial-module gate passed 3,271 tests with
zero failures and five environment skips (Windows symlink privileges and the
external Echo endpoint). Scoped check-rules and adversarial review were clean
self-review; package/provider, schema and architecture changes were not applicable.
