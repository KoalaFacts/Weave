# Security remediation — 2026-09-19

## Authorized scope

Work on `refactor/vertical-slice-foundation` / draft PR #89. Preserve the approved Vertical Slice and composition structure. No merge, deployment, database reset, external asset deletion, or production credential change is authorized by this work.

The implementation baseline is `87302b35e7c2a2118649a90aef0c754a740e9a8b`. The first regression test is `622a3a3a3e16d89eee3bd2a1a8d72407d94f469e`.

## Plan and acceptance

1. Reproduce the dependency gate and test the loaded native SQLite engine, not just its managed wrapper.
2. Pin patched transitive packages centrally without importing provider libraries into the generic product. Targets: Microsoft.OpenApi 2.7.5, MessagePack 2.5.302, SSH.NET 2026.0.0, SQLitePCLRaw.bundle_e_sqlite3 2.1.12 and native SQLitePCLRaw.lib.e_sqlite3 3.53.3. Regenerate the resolved lockfiles.
3. Add regression tests for signed-token identity/grant boundary ambiguity, exact timestamp integrity, culture-independent ordering, mutable request aliasing, malformed tokens and revocation path traversal. Observe their failure before changing the token service.
4. Replace ambiguous concatenation with a bounded, unambiguous, version-domain-separated signed payload. Authenticate token contents before consulting revocation storage. Reject invalid revocation identifiers before filesystem access. Keep changes within `src/Authority/Tokens`.
5. Remove the temporary diagnostic dependency-warning downgrade from branch validation. Require strict restore/audit, Release build, architecture checks and the full .NET suite. Only record generated lockfiles after every mandatory gate succeeds.
6. Review the changed security code and negative tests, inspect the exact CI commit and downloaded evidence, and update the PR with actual results and remaining limits.

## Baseline evidence

Run `35432494163` at the test-only commit compiled but failed the new SQLite test: actual engine `3.49.1`, required minimum `3.50.2`. Strict restore also failed the pre-existing dependency advisories. No suppression of these findings is a fix.

## Token-format rollout

The new signing payload intentionally invalidates tokens issued with the previous ambiguous format. Deploy issuers and validators together and re-mint capability tokens; do not add an old-format verification fallback. Current/previous signing-key rotation remains supported within the new format. No production key is changed by this PR.

This is a bounded security increment, not certification of every deployment or implementation of the future Room/operation-level governance model. Existing workspace-bound identities, tool-level grants, coverage attribution and independent review remain separately tracked work.

## Upstream evidence

- https://github.com/microsoft/OpenAPI.NET/security/advisories/GHSA-v5pm-xwqc-g5wc — patched 2.x release 2.7.5.
- https://github.com/MessagePack-CSharp/MessagePack-CSharp/releases — 2.5.302 includes the combined maintained-branch security fixes.
- https://github.com/sshnet/SSH.NET/releases/tag/2026.0.0 — includes the SCP path/name and protocol input fixes.
- https://github.com/ericsink/SQLitePCL.raw/releases — maintained 2.x bundle and updated native engine.
- https://www.nuget.org/packages/SQLitePCLRaw.lib.e_sqlite3/3.53.3 — native package version identifies the SQLite engine build.

Results are recorded only after execution. Local environment has no .NET SDK or working outbound DNS; C# build and runtime tests execute in the repository's GitHub Actions runner. Author self-review is not an independent security assessment.
