# Supply Chain Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remediate supply chain attack vectors identified in the security audit — covering dependency config, CI/CD pipelines, signing key safety, SSRF, and command injection.

**Architecture:** Five independent tasks that each harden a specific attack surface. No task depends on another. All can be implemented in parallel.

**Tech Stack:** .NET 10, GitHub Actions, MSBuild/NuGet, xunit.v3/Shouldly

---

## Task 1: Build & Dependency Hardening

**Files:**
- Create: `NuGet.config`
- Modify: `Directory.Build.props`
- Modify: `Directory.Packages.props`
- Modify: `global.json`

- [ ] **Step 1: Create NuGet.config** with `<clear/>` to prevent dependency confusion and add package source mapping.

- [ ] **Step 2: Add `RestorePackagesWithLockFile` and `NuGetAudit` settings** to `Directory.Build.props`.

- [ ] **Step 3: Clean up `Directory.Packages.props`** — remove unused `Verify.XunitV3` and `Microsoft.Extensions.AI.OpenAI`, align OpenTelemetry versions to 1.15.1.

- [ ] **Step 4: Pin SDK version in `global.json`** to `10.0.201` with `latestPatch` rollForward.

- [ ] **Step 5: Run `dotnet restore` to generate lock files**, then verify build.

- [ ] **Step 6: Commit.**

---

## Task 2: CI/CD Hardening

**Files:**
- Modify: `.github/workflows/ci.yml`
- Modify: `.github/workflows/release.yml`
- Modify: `.github/workflows/scan-security.yml`
- Modify: `.github/workflows/publish-nuget.yml`
- Modify: `.github/actions/setup-dotnet/action.yml`

**SHA pins:**
- `actions/checkout@v6` -> `de0fac2e4500dabe0009e67214ff5f5447ce83dd`
- `actions/upload-artifact@v7` -> `bbbca2ddaa5d8feaa63e36b76fdaad77386f024f`
- `actions/download-artifact@v8` -> `3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c`
- `actions/setup-dotnet@v4` -> `67a3573c9a986a3f9c594539f4ab511d57bb3ce9`
- `EnricoMi/publish-unit-test-result-action@v2` -> `c950f6fb443cb5af20a377fd0dfaa78838901040`
- `actions/dependency-review-action@v4` -> `2031cfc080254a8a887f58cffee85186f0e49e48`
- `actions/attest-build-provenance@v4` -> `a2bbfa25375fe432b6a289bc6b6cd05ecd0c4c32`

- [ ] **Step 1: Pin all third-party Actions to full SHA hashes** in all workflow files and the composite action.

- [ ] **Step 2: Fix script injection in `release.yml`** — replace `${{ inputs.version }}` in `run:` blocks with `env:` variable indirection.

- [ ] **Step 3: Fix script injection in `publish-nuget.yml`** — same pattern for `${{ inputs.version }}`.

- [ ] **Step 4: Remove `continue-on-error: true`** from dependency review step in ci.yml. Add `allow-ghsas` with empty list as placeholder.

- [ ] **Step 5: Scope permissions per-job in `release.yml`** — only `github-release` needs `contents: write`.

- [ ] **Step 6: Commit.**

---

## Task 3: Signing Key Fail-Fast

**Files:**
- Modify: `src/Security/Weave.Security/Tokens/CapabilityTokenOptions.cs`
- Modify: `src/Security/Weave.Security/Tokens/CapabilityTokenService.cs`
- Modify: `src/Runtime/Weave.Silo/appsettings.json`
- Modify: `src/Security/Weave.Security.Tests/CapabilityTokenServiceTests.cs`

- [ ] **Step 1: Remove default signing key** from `CapabilityTokenOptions.SigningKey` (make it `string?`).

- [ ] **Step 2: Remove fallback-to-default** in `CapabilityTokenService` constructor — throw `InvalidOperationException` if key is null/empty. Remove parameterless constructor.

- [ ] **Step 3: Add minimum key length validation** (32 chars minimum).

- [ ] **Step 4: Update `appsettings.json`** — keep the dev key but add a comment that it's dev-only.

- [ ] **Step 5: Update tests** — all tests must explicitly provide a signing key via `Options.Create(new CapabilityTokenOptions { SigningKey = "..." })`.

- [ ] **Step 6: Add test for missing signing key throws.**

- [ ] **Step 7: Add test for short signing key throws.**

- [ ] **Step 8: Build and run tests.** Commit.

---

## Task 4: OpenAPI SSRF Protection

**Files:**
- Modify: `src/Tools/Weave.Tools/Connectors/OpenApiToolConnector.cs`
- Modify: `src/Tools/Weave.Tools.Tests/OpenApiToolConnectorTests.cs`

- [ ] **Step 1: Add SSRF validation** to `InvokeAsync` — reject endpoints containing `..`, `://`, `\`, `%`, `@` (matching `DirectHttpToolConnector` pattern).

- [ ] **Step 2: Store auth per-tool** using a `ConcurrentDictionary` instead of mutating shared `HttpClient.DefaultRequestHeaders`.

- [ ] **Step 3: Add tests for SSRF rejection** — absolute URL, path traversal, encoded chars, backslash, @ sign.

- [ ] **Step 4: Build and run tests.** Commit.

---

## Task 5: CLI Shell Metacharacter Sanitization

**Files:**
- Modify: `src/Tools/Weave.Tools/Connectors/CliToolConnector.cs`
- Modify: `src/Tools/Weave.Tools.Tests/CliToolConnectorTests.cs`

- [ ] **Step 1: Add shell metacharacter detection** — reject commands containing `;`, `|`, `&&`, `||`, `` ` ``, `$(`, `\n` unless the entire command matches an allowed pattern exactly.

- [ ] **Step 2: Make wildcard matching case-insensitive** (`OrdinalIgnoreCase`).

- [ ] **Step 3: Add tests** for each metacharacter being rejected.

- [ ] **Step 4: Build and run tests.** Commit.
