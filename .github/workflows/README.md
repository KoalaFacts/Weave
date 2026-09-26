# GitHub Actions Workflows

Automated CI/CD workflows for Weave.

## Workflows Overview

### 🔨 [ci.yml](./ci.yml) — Build and Test

**Triggers:** Push to `main`, pull requests, merge queue, manual

**Jobs:**
1. **Build & Test** — restore, build, test, publish test results
2. **Code Quality** — dependency audit, warnings-as-errors build, formatting check
3. **Dependency Review** (PRs only) — blocks high-severity vulnerabilities and GPL licenses

**Artifacts:** Test results (30 days)

---

### 📦 [release.yml](./release.yml) — Create Release

**Trigger:** Manual (`workflow_dispatch`) — enter a version like `0.1.0`

**Jobs:**
1. **Create Release** — validate version, build, test, pack the Weave.Cli .NET tool, generate provenance attestation
2. **Publish Binaries** (×6) — single-file, self-contained, trimmed, R2R binaries for win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64
3. **GitHub Release** — creates a tagged release with all platform archives and auto-generated notes

**Artifacts:** NuGet package (90 days), platform binaries

---

### 🚀 [publish-nuget.yml](./publish-nuget.yml) — Publish to NuGet

**Triggers:**
- Automatic — when `Create Release` workflow completes successfully
- Manual — for emergency republishing

**What it does:**
1. Determines version from latest release tag
2. Downloads the Weave.Cli package from the release (or rebuilds from source)
3. Publishes the `.nupkg` and `.snupkg` to NuGet.org
4. Generates a job summary with install command

**Requirements:**
- `NUGET_API_KEY` secret configured
- GitHub environment: `nuget`

---

### 📦 [npm-plugin-echo.yml](./npm-plugin-echo.yml) — Verify npm Echo Plugin

**Triggers:** Echo package and connector changes on `main` or in pull requests, manual

Builds and tests the npm package, checks its published contents and dependencies,
then exercises the real Weave MCP connector and authorization path.

### 🚀 [publish-npm-plugin-echo.yml](./publish-npm-plugin-echo.yml) — Publish npm Echo Plugin

**Trigger:** Manual on `main` with the exact package version

The verification job checks the version, tests and packs the package without
OIDC permission. The publish job downloads that exact tarball, verifies its
checksum and uses npm trusted publishing. The `npm` environment restricts
deployment to `main` and requires a maintainer review. No npm write token is
used by this workflow. The first package version must be published interactively
before npm allows a trusted publisher to be configured; subsequent versions
use this workflow.

### ✅ [verify-published-npm-plugin-echo.yml](./verify-published-npm-plugin-echo.yml) — Verify Published npm Echo Plugin

**Triggers:** Called by the Echo publish workflow after npm publish succeeds,
or manual dispatch with the exact version at the selected commit

Installs the exact package from the npm registry, verifies its registry
signatures and source provenance, and runs its HTTP entry through Weave's
authorization, workspace-installation, restart, disable, and schema-drift
checks. The job has read-only repository permission and uses no npm publish
credentials. A successful source-package CI run alone does not prove this
published-package path.

---

### 🔒 [scan-security.yml](./scan-security.yml) — Security Scan

**Triggers:** Weekly (Monday 03:00 UTC), push to `main`, PRs, manual

**What it does:**
- Audits all dependencies for known vulnerabilities
- Checks for deprecated packages
- Uploads audit report

---

## Composite Actions

### [setup-dotnet](../actions/setup-dotnet/action.yml)
Reusable action that sets up .NET SDK with NuGet package caching. Used by all workflows.

---

## Release Process

```bash
# 1. Go to Actions → Create Release → Run workflow
# 2. Enter version: 0.1.0
# 3. The workflow will:
#    - Build, test, and pack everything
#    - Create platform binaries (6 architectures)
#    - Create a GitHub Release with all artifacts
#    - Trigger publish-nuget to push packages to NuGet.org
```

No manual git tagging required — the workflow creates the tag when it publishes the release.

---

## Required Setup

### Secrets

Configure in **Settings → Secrets and variables → Actions**:

| Secret | Description |
|--------|-------------|
| `NUGET_API_KEY` | NuGet.org API key with push permissions |

### Environments

Create a `nuget` environment in **Settings → Environments** for deployment protection rules (optional but recommended).

---

## Status Badges

```markdown
[![CI](https://github.com/KoalaFacts/Weave/actions/workflows/ci.yml/badge.svg)](https://github.com/KoalaFacts/Weave/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Weave.Cli.svg)](https://www.nuget.org/packages/Weave.Cli)
```

---

## Troubleshooting

### CI fails on formatting
```bash
dotnet format Weave.slnx
```

### NuGet publish fails with "already exists"
The `--skip-duplicate` flag handles this. If it still fails, verify the version isn't already published on NuGet.org.

### Release workflow can't find packages
The publish-nuget workflow will fall back to building from source if no packages are attached to the release.
