# Coverage scope and evidence

The repository requires at least 90% line coverage for every project, with no exceptions or per-project carve-outs. The tested-owner gate repairs the existing test-project-based command without silently converting it into a new all-runtime-project requirement. Its successful exit establishes only the named tested-owner scope, not broader policy compliance or permission to merge.

## Explicit ownership

`scripts/coverage-ownership.json` records each actual runtime project's assembly name, direct-reference and explicitly declared dynamic test owners, and allowed runtime-reference contributors. Architecture tests independently compare the manifest with all projects under `src/`, `hosts/`, and `extensions/`, and with direct references, the narrowly source-verified dynamic load below, and their transitive runtime closure. Build-only analyzer references are not runtime contributors. New projects and suites must update this evidence; observed XML cannot define the required inventory.

The current ten test projects directly exercise 19 runtime assemblies: 18 through project references and Dashboard through an explicit dynamic load. The required scope includes all non-excluded sequence points reported for each assembly, not only the feature under test. All ten suites may contribute to `Weave.Product`. The mailbox suite requires `Weave.Product`, `Weave.Mailboxes.Sqlite`, and `Weave.Mailbox.Host`; none can be absent or empty.

`tests/Weave.Silo.Tests/Invocations/DashboardReviewRenderingTests.cs`, method `DashboardReviewRenderingTests.DashboardType`, deliberately uses `Assembly.LoadFrom` on the real solution-built `Weave.Dashboard.dll`. Rendering, session, merge-regression, and host tests call it. The manifest records this single source-backed dynamic owner; the architecture regression verifies the exact relationship and loader evidence. Dashboard is therefore required for the Silo suite, and its runtime dependencies (including Deploy) may contribute from that report. This is not permission to accept arbitrary runtime assemblies or to exempt Dashboard from 90%.

The other six runtime assemblies are explicit coverage-scope gaps:

- `Weave.ServiceDefaults` and `Weave.Silo.Clustering.Postgres`, `.Redis`, `.SqlServer`, and `.Sqlite` are transitively reachable but have no direct test-project owner.
- `Weave.AppHost` has no current test contributor.

The analyzer prints measured lines and rates for additional runtime assemblies when permitted reports contain them; missing evidence is `N/A`, never 0% or 100%. These diagnostic rows do not enforce a newly invented threshold or silently assert that the broader policy has been met. Resolving the broader ownership/policy gap is a separate decision.

## Evidence rules

- Each selected suite must have its exact `TestResults/<suite>.cobertura.xml` report and some non-excluded required-assembly line evidence. Extra or unselected reports cannot substitute for missing selected reports.
- Every required assembly needs valid line evidence across its permitted reports. Empty, malformed, missing, mismatched, or entirely excluded evidence fails closed.
- A line is identified by actual case-sensitive assembly identity, canonical repository-relative source filename, and positive line number. Hits are nonnegative integers. A line is covered if any permitted report or class entry reports positive hits; duplicate entries never enlarge the denominator.
- Source roots and both path separators are normalized against project files. Ambiguous or unresolved owned filenames fail instead of disappearing. Distinct files with the same basename remain distinct.
- Whole-assembly scope is preserved, including zero-hit Product lines outside Contacts/Mailboxes. A rounded display percentage or overall average never rescues a required assembly below 90%.
- Existing exclusions are unchanged: `obj` path components, `.g.cs`, `Models` path components, filenames containing `Surrogate`, `*Contracts.cs`, and `Program.cs`.

The collector must actually observe the test processes. Synthetic fixtures prove analyzer behavior only. Collection must be followed by inspection of actual XML, including actual module identities, source paths, zero-hit lines, and the included source scope. An IPC permission failure, missing module, or successful test run without coverage evidence leaves the percentage unavailable.

## Running and reviewing

Use the commands in [best practices](best-practices.md#test-coverage--hard-rule-90-minimum), from the repository root after building the solution in Release. The pinned Microsoft collector is `dotnet-coverage` 18.11.2. `--root tests/Weave.Mailboxes.Tests` is a focused diagnostic and cannot establish the full tested-owner result. `--skip-collect` never searches unrelated reports to rescue a selected suite.

CI restores the pinned tool and locked DevTool dependencies, runs the actual executable regression suite, collects every test project with normal `dotnet test`, enforces the tested-owner gate at 90%, and retains `TestResults/` even on failure. Collector subprocess output is inherited rather than placed in unread redirected pipes. A fresh collection deletes each selected report before starting that suite, preventing a stale success from being accepted after collection failure.

Exit codes: 0 means the selected tested-owner scope meets the threshold; 1 means observed below-threshold coverage or failed collection; 2 means invalid/missing evidence or invalid inventory/options. Record the exact commit and per-assembly results before making coverage claims.
