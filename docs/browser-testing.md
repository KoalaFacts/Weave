# Browser tests

Dashboard and Worker browser tests use official Playwright SDKs with the managed `chromium` channel. Dashboard pins `Microsoft.Playwright` in `Directory.Packages.props`; Worker pins `playwright` in its npm manifest and lockfile. The current .NET 1.62.0 and Node 1.62.1 SDK manifests both select Chromium revision 1234 (Chrome for Testing 151.0.7922.34). Browser revisions must be checked when either SDK changes; do not substitute a system Chrome executable.

## Dashboard

From the repository root, restore and build the solution, then install the browser through the built SDK:

```bash
dotnet restore Weave.slnx --locked-mode
dotnet build Weave.slnx --no-restore -c Release
pwsh tests/Weave.Silo.Tests/bin/Release/net10.0/playwright.ps1 install --with-deps --no-shell chromium
dotnet test --project tests/Weave.Silo.Tests/Weave.Silo.Tests.csproj --no-build -c Release \
  -- --filter-class Weave.Silo.Tests.Dashboard.DashboardReviewBrowserTests
```

The five review cases exercise real Blazor file uploads, exact request bytes, safe denial, upstream cancellation, clearing credential fields, literal rendering and invalid UTF-8/oversized rejection before upstream dispatch. A fresh browser context owns its page and permits only the fixture's Dashboard HTTP/WebSocket origin. Service workers and redirects are blocked. Native file-input listener readiness is observed before uploading, because a visible server-rendered input is not necessarily interactive yet. The Dashboard server and temporary API/storage retain their independent bounded process cleanup.

## Worker

Use the [Worker browser instructions](../hosts/Weave.Mailbox.Worker/README.md) to install locked Node dependencies and managed Chromium, then run `npm run test:browser`. The browser suite covers the registry, independent accounts, recipient acceptance, exact-envelope retry after a lost successful response, inbox reads and mobile ACK. Its fixture regressions cover native browser operations and network/lifecycle boundaries. Inspect the synthetic screenshots as well as the assertion results.

## Environment and evidence

Use Linux with browser sandbox support. Both fixtures explicitly enable Chromium's sandbox through Playwright's supported option; Worker also asserts namespace and seccomp status. Playwright's unconfigured launch defaults can disable Chromium's sandbox, so a successful default smoke page does not prove protected acceptance. Do not add disabling flags, custom executable paths, system-policy changes or automatic fallback to get a green result.

`PLAYWRIGHT_BROWSERS_PATH` can select an existing official managed cache. A bare default headless launch expects a separate headless-shell installation; these fixtures select the full `chromium` channel installed with `--no-shell`. Tests never download browsers themselves. On a denied download, retain the error and stop rather than changing mirrors or credentials.

CI and release verification install Dashboard's pinned managed browser after the .NET build; Worker verification installs its browser after locked npm installation. Existing test/coverage/audit gates remain. Installing a browser is a prerequisite, not a passing test. Startup errors, absent screenshots, failed assertions, skips and dependency/source differences must be reported separately; local evidence does not establish an unsynchronized main commit or remote deployment result.
