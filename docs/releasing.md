# Releasing Weave

首个公开预览版 `0.1.0-preview.1` 已发布。仓库构建成功不代表对应版本已在
NuGet 或 GitHub Releases 上线；使用前仍须核对具体版本及产物。

## Distribution scope

| Artifact | Consumer | Contents |
| --- | --- | --- |
| `KoalaFacts.Weave.Cli` NuGet tool | Operators with a stable .NET 10 SDK/runtime | The `weave` command and its CLI dependencies. |
| `weave-VERSION-RID.zip` or `.tar.gz` | Operators running Governed Tools | Self-contained CLI plus the matching Host in `host/`. |
| `KoalaFacts.Weave` project package identity | Future .NET library consumers | Reserved in source for a later, separately verified library release; the current workflow does not publish it. |

The public NuGet ID `Weave` belongs to an unrelated project. Package identities
use the `KoalaFacts` prefix; retained assemblies and namespaces do not change.
An absent NuGet listing does not prove that a name is reserved for this project.

The first release covers Governed Tools. The independent-agent mailbox host,
its MCP adapter and hosted services have separate deployment instructions and
are not included in these archives. HTTP clients can connect to a running,
pinned Host without building its source. The current AgentPort worker also
loads the Python bridge from `examples/governed-tools/codex_bridge.py` in a
Weave checkout; migrating that adapter is separate from this release.

## Consume a published version

Select an exact version that exists in both the
[GitHub release](https://github.com/KoalaFacts/Weave/releases) and
[NuGet listing](https://www.nuget.org/packages/KoalaFacts.Weave.Cli).
例如，选择并核对已发布的预览版后：

```bash
dotnet tool install --tool-path ./weave-tools KoalaFacts.Weave.Cli --version VERSION
```

Replace `VERSION` with that exact version. The NuGet tool alone does not install
a Host. For local governed execution, download and extract the same version's
platform archive and use its `weave` executable. Keep its adjacent `host/`
directory intact. `weave local init --help` describes setup; an explicitly
supplied `--host` must point to the matching published Host executable or DLL.

## Release prerequisites

Publishing uses [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
with GitHub OIDC. No long-lived NuGet API key is required or accepted as a fallback.
The operator needs NuGet ownership or publish permission for
`KoalaFacts.Weave.Cli` and must register this policy under the intended NuGet
user or organization:

| Trusted Publishing field | Value |
| --- | --- |
| Repository owner | `KoalaFacts` |
| Repository | `Weave` |
| Workflow file | `publish-nuget.yml` (file name only) |
| Environment | `production` |
| Package pattern | `KoalaFacts.Weave.Cli` |
| Publishing scope | Allow the first package creation and subsequent version publication. |

Set the `NUGET_USER` environment variable in the GitHub `production` environment's
**Variables** settings to the personal NuGet profile username that created the
policy, even when the policy owner is an organization. Do not use the organization
name, an email address or an assumed GitHub username. This is a GitHub Actions
configuration variable, not a shell variable or API key. Restrict that environment
to the `main` branch. An absent package listing does not establish publisher
ownership; verify the NuGet policy owner and package permissions before first use.

After artifact validation, the publish job requests an OIDC token and exchanges
it through the pinned official `NuGet/login` action for a short-lived publishing
key. Only that action's masked output is supplied to `dotnet nuget push`.
Neither the source-verification job nor ordinary PR checks can request a NuGet
publishing token. Missing username configuration or a rejected trust policy
fails publication without falling back to a stored key.

The release source must be a reviewed commit on `main`. Choose an unused
SemVer version, such as `0.1.0-preview.1`, and use that same version for the
assembly build, NuGet package, native archives and Git tag. A preview version
must be marked as a GitHub prerelease.

## Verification and publication

`Create Release` runs the locked restore, Release build, test suite and package
validation. Its installation smoke test installs only the newly built package
from an isolated local feed and checks the executable version. Native platform
jobs additionally exercise the published CLI and Host outside the source tree:
submit a synthetic document write, approve it with a separate test identity,
resume the same invocation, and verify the result. This is automated protocol
verification, not evidence of human review or production isolation.

The workflow builds archives for six runtime identifiers. Cross-built archives
must not be described as having passed a native runtime test unless such a
test actually ran. Check each matrix job's evidence and platform limits.

After all required jobs succeed, the workflow creates the version tag and
GitHub release. `Publish to NuGet` then validates the successful source run,
immutable package artifact and tag before publishing that artifact. It does
not rebuild from a moving branch or select the latest release implicitly.

For a failed NuGet publication, use the manual publish workflow with the exact
successful `Create Release` run ID and version. Do not choose a new version or
rebuild merely to bypass a failed source validation. A GitHub release alone is
not proof that NuGet publication completed; inspect both destinations.

Before announcing a release, install the published NuGet version in a clean
directory and verify the downloaded platform archive. Record the version,
source commit, workflow runs and the platforms actually exercised. Do not
label a source ZIP or a local test package as a published release.
