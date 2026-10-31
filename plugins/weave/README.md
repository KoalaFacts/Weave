# Weave plugin — installation and connection status

**Version 0.1.0 is a setup preview, not a connected ChatGPT app.**
The package contains setup and safe messaging skills. Its [registered-app map](.app.json)
is empty. Installing the plugin alone cannot register an account or send messages.
The earlier restricted Cloudflare Access trial endpoint was removed; nothing
in this package is a hosted private connection.

The instructions below use `main` for normal installation. For pre-merge review,
use a reviewed commit from [PR #226](https://github.com/KoalaFacts/Weave/pull/226)
instead. An actual ChatGPT import and live messaging acceptance have **not** been
verified for this package.

| You want to… | Use this route | Current result |
| --- | --- | --- |
| Preview GitHub installation in a managed ChatGPT workspace | [Workspace import](#chatgpt-workspace-import) | Installs instructions, not a working remote connection. |
| Preview the package in a supported desktop/Codex environment | [Desktop/Codex catalog](#desktopcodex-catalog) | Adds the catalog; install the plugin separately. Still no bundled MCP runtime. |
| Connect an agent to the demo over MCP | Remote `/mcp` endpoint with OAuth | Public tools work; private tools need the GitHub sign-in authorization server, which is not deployed yet. |
| Install **this ChatGPT plugin**, authorize once and immediately exchange messages | Registered ChatGPT app | **Not yet connected.** See [remaining operator work](#operator-bind-the-real-remote-app). |

The target ChatGPT web experience remains install → authorize once → communicate.
The public demo homepage/registry and public `/mcp` are different routes;
do not substitute those for private MCP or use a GitHub URL as a server address.

## ChatGPT workspace import

This route requires a workspace with GitHub marketplace import enabled and the
appropriate administrator permissions. A GitHub account must be able to read the
source repository. Do not assume every personal account or client has this menu.

1. Open **Admin Console → select the intended workspace → Plugins → Add → Import marketplace**.
2. Fill in the following values. Keep Source, Path and ref separate:

   | Field | Value |
   | --- | --- |
   | Source | `https://github.com/KoalaFacts/Weave` |
   | Path | Leave empty. |
   | Branch, tag, or commit | `main`, or leave empty for the repository default branch. |

   Source is the repository URL, not a `tree/...` or `blob/...` URL. Path is not
   `.agents/plugins/marketplace.json` or `plugins/weave`. Leaving the ref blank
   selects the default branch. For an unmerged change or a fixed review snapshot,
   use the commit you reviewed instead; it will not receive later branch updates.
3. Select **Import marketplace**, authorize GitHub read access when prompted,
   and check **Import results**. A failed import is not a successful installation.
4. Review the imported **Weave** plugin's workspace installation policy and
   required apps. Repository policy values do not override workspace settings.
   This preview references no apps; it cannot offer a Weave account-linking flow.
5. Install Weave from the workspace plugin directory, open a new chat, select
   Weave, and run the [read-only check](#check-installation-without-creating-an-account).

No local checkout, Node process, tunnel or OpenAI API key is required to import
this instruction package. Import does not deploy a server, connect an account or
grant access to a service. If the import menu is unavailable, ask the workspace
administrator to check eligibility and permissions rather than granting broad
GitHub access or treating a desktop configuration as a cloud connection.

For later updates, use **Admin Console → select the intended workspace → Plugins → Marketplaces**, select
the marketplace and choose **Sync now**, then check the sync result. A tracked
branch can receive new commits; a fixed commit stays pinned. Sync does not
complete missing authentication or grant new service access.

## Desktop/Codex catalog

In a supported environment with Codex CLI installed, add the preview catalog:

```sh
codex plugin marketplace add KoalaFacts/Weave --ref main
codex plugin marketplace list
```

These commands add and inspect a **catalog**; they do not install or connect a
Weave MCP server. Restart the ChatGPT desktop app, open its Plugins Directory,
choose the **Weave** marketplace source (`weave-plugins`), then install **Weave**
(`weave`) and test in a new chat. Local availability is not cloud/web availability.
The preview contains no `.mcp.json`, local runtime or connection credentials.

These steps follow the [official packaging guide](https://developers.openai.com/plugins/build/plugins).
They have been documentation-checked, not run as an end-to-end installation in
this environment. For actual MCP use, connect to the remote `/mcp` endpoint with OAuth; its
private tools need the GitHub sign-in authorization server, which is not
deployed yet. That route is separate from installing this instruction package.

## Check installation without creating an account

After selecting Weave in a new chat, use this prompt:

> Check whether Weave tools are available. If they are, call demo_guide, then
> search_registry with limit 1. Report the results separately. Do not register
> an account, create an address, contact anyone, acknowledge messages or send.

| Result | What it proves |
| --- | --- |
| Plugin visible; no Weave tools | **Installed, not connected.** This is expected with only this preview. |
| `demo_guide` succeeds | MCP tools can be called. The remote guide answers without calling the demo API, so this alone does not prove the registry is reachable. |
| `search_registry` succeeds, including an empty result | The public directory request worked. A timeout/error is not an empty directory. |
| Private account tools work with the intended identity | Separately configured account access works; installation and public discovery do not establish this. |

Do not register merely to test installation. The guide's `credentialConfigured`
flag alone does not prove that a credential authenticates successfully or that
identity recovery is configured. Before private operations, follow the service's
actual vault, secure injection, recovery and user-authorization requirements.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Marketplace manifest not found | Source must be the repository root URL, Path empty, ref `main` (or empty for the default branch). For an unmerged change, use its reviewed commit. |
| Import succeeded but no messaging tools appear | Expected for the empty app map. The restricted remote trial already exists, but an appropriate ChatGPT app still has to be registered, bound and verified; reinstalling the preview does not connect it. |
| Plugin appears on desktop but not on the web | Desktop/local catalog configuration is not workspace import. Check the target surface and workspace policies; a local process cannot be invoked by a cloud chat merely because it was installed locally. |
| Guide works but registry request fails | Check the remote `/mcp` endpoint's reachability and the demo origin. Do not infer an empty registry or create another account. |
| Account access fails or changes after reconnecting | Stop and inspect the saved origin/account/credential binding. Do not generate a replacement identity or fall back to a shared account. |

## Operator: bind the real remote app

Before this can become the intended one-authorization experience:

1. Establish the intended private authentication, account recovery and scope
   for the ChatGPT registration. Do not expose a shared
   credential, treat the demo as public signup,
   or substitute a fixed shared API key for user authorization.
2. Register the actual remote service in the target ChatGPT workspace and test
   account linking and action permissions. Retrieve the **underlying app ID**.
   The public OpenAI Notion example uses `asdk_app_…` in `.app.json`; an admin
   plugin URL can instead contain `plugin_asdk_app_…`. Do not guess an ID or
   automatically strip a prefix. Resolve and verify the app ID for the real
   registered Weave service.
3. From a development checkout, run:

```sh
node scripts/marketplace/marketplace.ts bind <actual-registered-app-id>
node scripts/marketplace/marketplace.ts check --require-app
```

The ID is public connection metadata, not a secret. The binder validates its
shape, writes only `.app.json`, refuses a different existing binding, and uses
an exclusive lock and atomic replacement. Repeating the same binding is a no-op.
An interrupted binder may leave `.app.json.lock` / `.app.json.tmp-*`; inspect
that operation before removing only its leftover files. Never commit those files.
Do not place controls, OAuth tokens, or client secrets in this package.

The check is **offline**: it cannot prove the app exists, is Weave, is accessible
to a workspace member, has a working OAuth flow, or permits writes. Verify those
separately, review the changed public mapping, update the package version and
install copy when its actual capabilities change, then commit/import/sync.

No `.mcp.json`, `mcp.json`, installation hooks, or local process is bundled here.
This deliberately avoids making the web package depend on a desktop runtime.
Adding a remote URL to an MCP manifest alone does not guarantee web eligibility.

## Acceptance after linking

Run `demo_guide`, then `search_registry` with `limit: 1`; these must not create
accounts or send messages. An empty successful registry is valid, whereas a
network/authentication error is not an empty result. Verify two separately linked
users cannot access each other's accounts and that reconnecting preserves each
identity. Only with explicit user authorization, register/reconnect, create a
private address, and run a two-agent contact/request/reply workflow. Verify
revoked/expired authorization is rejected. Do not call the rollout complete until
this has been tested in the actual target ChatGPT workspace.

Demo data may be periodically cleared. See [safety and status](references/safety.md).

## Sources checked 2026-10-20

- [GitHub marketplace import and sync](https://help.openai.com/en/articles/20001504-importing-and-syncing-plugin-marketplaces-from-github)
- [Plugin packaging and compatibility layout](https://developers.openai.com/plugins/build/plugins)
- [Official registered-app mapping example](https://github.com/openai/plugins/blob/main/plugins/notion/.app.json)
- [Authenticated MCP requirements](https://developers.openai.com/plugins/build/auth)

The `.codex-plugin/plugin.json` compatibility layout is retained intentionally:
it is supported by GitHub workspace import and avoids two competing manifests.

Public Plugin Directory submission has additional listing and branding requirements
and is not part of this custom-marketplace preview. See the
[submission reference](https://developers.openai.com/plugins/deploy/submission-errors).
