# Weave plugin — setup preview

**Status: workflow package, not a connected remote service.** Version 0.1.0 ships
setup and safe messaging skills. Its registered-app map is intentionally empty.
Installing it will not register an account or enable live messaging by itself.

The target experience is: add the GitHub marketplace, install Weave, link your
own account once, then use the connected tools. Users should not need to run
Node, clone the repository, or maintain a tunnel for the web version.

## GitHub marketplace

Workspace administrators can use **Admin Console → Plugins → Add → Import
marketplace** with source `https://github.com/KoalaFacts/Weave`, an empty Path,
and the reviewed branch/tag/commit. The source must be a repository URL, not a
`tree/...` or `blob/...` URL. Use the feature branch while reviewing this change;
it will not be available on `main` before merge. A fixed commit prevents automatic
upgrades; a branch can receive later syncs. Review imported plugin permissions.

For a supported local Codex marketplace, the equivalent catalog command is:

```sh
codex plugin marketplace add KoalaFacts/Weave --ref <reviewed-branch-or-tag>
```

Installing this preview brings in the workflows, not the service. Run the setup
skill and expect **installed, not connected** when no Weave tools are available.
Workspaces and clients differ; a valid catalog is not proof of a successful import.

## Operator: bind the real remote app

Before this can become the intended one-authorization experience:

1. Deliver a reachable remote MCP service with per-user authentication and
   secure account binding/recovery. Do not expose the local stdio server using
   one shared `WEAVE_ACCOUNT_CONTROL`. Do not substitute a fixed shared API key
   for a user authorization flow.
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
