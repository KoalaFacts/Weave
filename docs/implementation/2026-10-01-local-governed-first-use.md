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
- Scoped check-rules and adversarial self-review covered initialization, retained
  state, process arguments/environment, transport bounds, lost responses, human
  decision input, meaningful regressions and documentation. No remaining findings;
  no independent reviewer or cross-platform runtime acceptance is claimed.
