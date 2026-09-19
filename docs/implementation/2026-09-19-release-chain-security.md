# Release-chain security hardening — 2026-09-19

## Scope and base

This increment is stacked on PR #89 at `deaca1b61f2e6bd382ccdfb499c1949a12b42f70`.
It preserves the flat source layout and the token/authentication fixes already on that branch.
It neither merges nor publishes nor modifies production keys, persisted data or repository settings.

## Confirmed defects and changes

1. A local regression test executed the existing release-version validation shell with a harmless
   command-substitution sentinel. The sentinel was created **before** the old regular expression
   validated the input. Inputs now enter Python through environment variables; all release shell
   scripts avoid GitHub expression interpolation. Version outputs are bounded canonical SemVer
   without build metadata, whitespace or leading-zero numeric identifiers.
2. Publishing used the first/latest repository release rather than the triggering build, and rebuilt
   current source if release packages were absent. Both paths are removed. Publishing requires a
   completed successful, same-repository, main-branch `release.yml` dispatch, exactly one unexpired
   package artifact, and a matching release tag/commit. Artifact IDs and run IDs are explicit;
   digest mismatch is fatal. There is no build in the job that receives the NuGet key.
3. Package archives must contain the expected `Weave.Cli` ID and exact version. Missing packages,
   unexpected files, symlinks, duplicate/oversized manifests and XML declarations with DTD/entities
   are rejected. This checks identity, not a guarantee about every byte of package code.
4. The release tag is created with the exact built commit as its target. Publishing independently
   checks the tag target, including bounded annotated-tag dereferencing.
5. NuGet credentials are supplied only to the push step through a quoted environment variable.
   The publish workflow no longer requests unused OIDC/attestation writes. CI defaults to read-only
   contents; test-check and dependency-review writes remain scoped to their respective jobs.
6. Shared SDK defaults match the existing `global.json` (10.0.201, GA); caching uses lockfiles.
7. MessagePack is advanced from 2.5.302 to **2.5.303**, the maintainer's September 17 security patch.
   The earlier successful advisory scan is not proof that newly published advisories are absent.
   Other reviewed dependency pins, especially the native SQLite fix, are preserved.

Primary references:
- https://github.com/MessagePack-CSharp/MessagePack-CSharp/releases/tag/v2.5.303
- https://github.com/MessagePack-CSharp/MessagePack-CSharp/security/advisories/GHSA-qhrr-8q5h-9q3h
- https://docs.github.com/en/actions/concepts/security/script-injections
- https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#workflow_run

## Operator-visible behavior

Create Release must run on `main`. Emergency NuGet republishing now requires **both** the exact
version and a successful Create Release run ID. An expired/deleted artifact, moved tag, failed run,
wrong branch/repository/workflow or mismatched version stops publishing; there is no fallback rebuild.
A fresh release under a new version is needed when verified artifacts no longer exist.

The existing `nuget` environment is retained. Required reviewers, environment branch restrictions,
main-branch required checks/reviews, secret scanning and push protection are administrator settings,
not enabled merely by this commit. The inspected `Proctected` ruleset only prevents deletion and
non-fast-forward updates. Other active rules and legacy protection must also be reviewed.

## Verification and limits

The five initial workflow/dependency regression tests failed against the base, including actual
local execution of the harmless injection sentinel. Added guard tests exercise positive and negative
source, artifact, version, package and annotated-tag cases without live publishing.

Run:

```sh
python3 -m unittest discover -s scripts/tests -v
dotnet restore Weave.slnx --force-evaluate
dotnet restore Weave.slnx --locked-mode
dotnet build Weave.slnx --no-restore -c Release
dotnet test --solution Weave.slnx --no-build -c Release
```

The dedicated `Security Release Chain Validation` workflow also scans all direct/transitive
NuGet dependencies and builds actual `.nupkg`/`.snupkg` tool packages, validating them without
publishing. It records only allowlisted lockfiles after all mandatory steps succeed and only while
the branch still has the expected head. Exact run IDs, source hashes and counts belong in the PR
verification record; a previous run's success must not be claimed for a new commit.

Live NuGet publication, all six native release targets, environment approvals and end-to-end GitHub
release creation are not exercised by this security validation. The implementation is author-reviewed,
not independently penetration-tested. Full tenant/admin authorization, plugin isolation, fine-grained
operation grants and other product security boundaries remain separate work described in PR #89.
