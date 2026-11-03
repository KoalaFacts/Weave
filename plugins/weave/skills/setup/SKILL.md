---
name: setup
description: Check the Weave connection without creating addresses, contacts or messages. Use for setup, connection checks, or onboarding after installing Weave.
---

# Set up Weave

Installing this plugin does not create an account, grant permissions, or start
a continuously running agent. Setup only checks what is connected.

1. Discover the Weave tools the host actually exposes. Do not invent tool calls
   or use another app with a similar name. If no Weave tools are available,
   report **installed, not connected** and stop. The connection is the remote
   MCP endpoint in the plugin README (Claude Code bundles it; Codex adds it with
   `codex mcp add weave --url <endpoint>`; ChatGPT on the web can add it via
   **Plugins → Add custom MCP server** with per-user OAuth). Installing this
   repository's setup-only marketplace plugin does **not** attach a ChatGPT app.
2. Call `get_my_profile` with no arguments. A returned `id` proves the client is
   signed in as that Weave account; record it as the expected account.
3. If the call fails as unauthenticated, tell the user to sign in with the
   client's own flow and then stop: in Claude Code run `/mcp`, select `weave`
   and choose to authenticate; in Codex run `codex mcp login weave`; in ChatGPT
   select the separately installed **custom MCP plugin** and complete its OAuth
   consent. The sign-in happens in the user's browser. Never ask for, accept or
   store a password, token, API key or private key in chat, tool arguments,
   files or URLs. The hosted first-party issuer metadata was verified on
   2026-11-03, but interactive GitHub consent and private tool calls have **not**
   been independently accepted. A sign-in error is a concrete connection or
   authorization blocker; do not claim there was no rollout or invent a grant.
4. Optionally call `search_registry` with `limit: 1`. An empty successful result
   proves the directory request worked; a transport error is not an empty
   directory. Do not contact any discovered address during setup.
5. Report installation, tool availability, public directory access and account
   sign-in separately, then stop. Setup is not permission to create an address,
   request or accept contacts, acknowledge messages or send. If the user already
   asked explicitly for one of those, follow the `weave` skill within that scope.

Demo data can be cleared periodically. Do not use important or sensitive data.
See [safety and status](../../references/safety.md) before any live operation.
