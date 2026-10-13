# Browser tests

Dashboard and Worker browser tests use official Playwright SDKs with the managed `chromium` channel. Dashboard pins `Microsoft.Playwright` in `Directory.Packages.props`; Worker pins `playwright` in its npm manifest and lockfile. The .NET 1.63.0 SDK selects Chromium revision 1243 (Chrome for Testing 153.0.8010.12); the Node 1.62.1 SDK selects revision 1234 (151.0.7922.34). Each suite installs its own SDK's browser. Browser revisions must be checked when either SDK changes; do not substitute a system Chrome executable.

## Dashboard

From the repository root, restore and build the solution, then install the browser through the built SDK:

```bash
dotnet restore Weave.slnx --locked-mode
dotnet build Weave.slnx --no-restore -c Release
pwsh tests/Weave.Silo.Tests/bin/Release/net10.0/playwright.ps1 install --with-deps --no-shell chromium
dotnet test --project tests/Weave.Silo.Tests/Weave.Silo.Tests.csproj --no-build -c Release \
  -- --filter-class Weave.Silo.Tests.Dashboard.DashboardReviewBrowserTests \
     --filter-class Weave.Silo.Tests.Dashboard.DashboardPlaywrightBrowserTests
```

The five review cases exercise real Blazor file uploads, exact request bytes, safe denial, upstream cancellation, clearing credential fields, literal rendering and invalid UTF-8/oversized rejection before upstream dispatch. A fresh browser context owns its page and permits only the fixture's Dashboard HTTP/WebSocket origin. Service workers and redirects are blocked. Native file-input listener readiness is observed before uploading, because a visible server-rendered input is not necessarily interactive yet. The Dashboard server and temporary API/storage retain their independent bounded process cleanup.

The fixture regressions also check foreign-origin HTTP/WebSocket denial followed by a live Blazor action, page reload with an active routed WebSocket, and disposal during a pending page operation. Reload and disposal make the [Playwright driver emit WebSocket close events without a code or reason](https://github.com/microsoft/playwright/blob/v1.63.0/packages/playwright-core/src/server/dispatchers/webSocketRouteDispatcher.ts). The [official .NET 1.63.0 SDK handles these optional fields](https://github.com/microsoft/playwright-dotnet/blob/v1.63.0/src/Playwright/Core/WebSocketRoute.cs); 1.62.0 threw `KeyNotFoundException` and could mask the original operation during cleanup. The SDK upgrade preserves routing and browser sandbox options.

## Worker

Use the [Worker browser instructions](../hosts/Weave.Mailbox.Worker/README.md) to install locked Node dependencies and managed Chromium, then run `npm run test:browser`. The browser suite covers the registry, independent accounts, recipient acceptance, exact-envelope retry after a lost successful response, inbox reads and mobile ACK. Its fixture regressions cover native browser operations and network/lifecycle boundaries. Inspect the synthetic screenshots as well as the assertion results.

Worker CI runs the same build, browser test files and concurrency with Node's standard multi-reporter support. Full `spec` output is retained in `test-results/browser/test.log`; a separate reporter emits at most ten bounded failure annotations with credential-like values omitted and repository test locations. Raw test output stays outside GitHub's workflow-command parser. These annotations supplement the existing evidence artifact and do not change the test runner's exit status.

Mobile evidence uses viewport tiles, named `<state>-tile-01.png`, `<state>-tile-02.png`, and so on. `<state>-tiles.json` records the unchanged viewport, document dimensions and each tile's scroll coordinates. The last tile can overlap its predecessor. Review the whole sequence for full-document coverage; desktop evidence remains a single full-page PNG. Capture restores the original scroll position, masks controls on every tile, counts every PNG against the existing evidence budget, and fails if the document changes or complete coverage exceeds the budget.

This avoids a Chromium 151 screenshot side effect without changing touch capability or mobile geometry. [Playwright 1.62.1 requests capture beyond the viewport for tall full-page screenshots](https://github.com/microsoft/playwright/blob/v1.62.1/packages/playwright-core/src/server/chromium/crPage.ts#L232-L254). [Chromium temporarily sets and restores WebPreferences during that capture](https://chromium.googlesource.com/chromium/src/+/refs/tags/151.0.7922.34/content/browser/devtools/protocol/page_handler.cc#1499); [applying those preferences writes the host's touch-point count](https://chromium.googlesource.com/chromium/src/+/refs/tags/151.0.7922.34/third_party/blink/renderer/core/exported/web_view_impl.cc#1698), and [that setter writes directly to page settings](https://chromium.googlesource.com/chromium/src/+/refs/tags/151.0.7922.34/third_party/blink/renderer/core/exported/web_settings_impl.cc#427). This source path explains the observed loss of `navigator.maxTouchPoints` after mobile capture. The public `page.screenshot({ fullPage: false })` API avoids it. A native browser regression checks touch points, coarse pointer capability, trusted touch events, viewport and scroll after repeated mobile and desktop captures. The original mobile layout and sandbox assertions remain required.

## Environment and evidence

Use Linux with browser sandbox support. Worker retains its original namespace and seccomp assertions. Dashboard had no sandbox-status assertions before the migration; its previous native Chrome launch used the normal sandbox defaults. The migration's explicit `ChromiumSandbox = true` preserves that launch behavior through Playwright's supported option. Playwright's unconfigured launch defaults can disable Chromium's sandbox, so a successful default smoke page does not prove protected acceptance. Do not add disabling flags, custom executable paths, global security-policy changes or automatic fallback to get a green result.

### Ubuntu CI prerequisite

The browser jobs pin `ubuntu-24.04`. Ubuntu's shipped Chrome AppArmor profile covers `/opt/google/chrome/chrome`, while Playwright installs its managed browser under the runner's cache. [Chromium documents a per-executable `userns` profile](https://chromium.googlesource.com/chromium/src/+/main/docs/security/apparmor-userns-restrictions.md); [Ubuntu explains the path-specific policy](https://ubuntu.com/blog/ubuntu-23-10-restricted-unprivileged-user-namespaces). A managed executable outside that profile needs its own namespace permission when Ubuntu's restriction is enabled.

After installation, the shared [sandbox prerequisite action](../.github/actions/setup-playwright-sandbox/action.yml) asks each suite's own pinned SDK for `chromium.executablePath()`. It accepts only an installed, executable, nonsymlink browser at the default managed cache path and renders one exact AppArmor attachment without wildcards. When Ubuntu's user-namespace restriction is enabled, it loads that root-owned profile on the ephemeral GitHub-hosted runner with `apparmor_parser`; it does not reload unrelated profiles or change global sysctls. If the runner already permits user namespaces, no profile is loaded. The `unconfined` profile flag follows Chromium's documented user-namespace exception; Chromium still supplies its namespace and seccomp sandbox. The action records the SDK version, browser path/hash, runner version and policy state, and fails if the global namespace restrictions change. This CI configuration is not applied by tests or used to change a restricted local execution environment.

The cache remains writable by the runner account, so this profile is scoped to a disposable CI job, not a policy for a shared host. Namespace/seccomp assertions, network boundaries, full UI checks and screenshot review remain the acceptance evidence; profile installation alone does not establish any of them.

`PLAYWRIGHT_BROWSERS_PATH` can select an existing official managed cache. A bare default headless launch expects a separate headless-shell installation; these fixtures select the full `chromium` channel installed with `--no-shell`. Tests never download browsers themselves. On a denied download, retain the error and stop rather than changing mirrors or credentials.

CI and release verification install Dashboard's pinned managed browser after the .NET build; Worker verification installs its browser after locked npm installation. Existing test/coverage/audit gates remain. Installing a browser is a prerequisite, not a passing test. Startup errors, absent screenshots, failed assertions, skips and dependency/source differences must be reported separately; local evidence does not establish an unsynchronized main commit or remote deployment result.
