---
name: setup
description: Check Weave installation and connection readiness without creating accounts or sending messages. Use for setup, connection checks, or onboarding after installing Weave.
---

# Set up Weave

A GitHub marketplace installs these instructions. It does not deploy a server,
connect an account, grant permissions, or start a continuously running agent.
This package is a setup preview until a real Weave app is registered and linked.

1. Discover the actual tools exposed by the connected Weave app. Do not invent
   tool calls or use another app with a similar name. If no Weave tools are
   available, report **installed, not connected** and stop. The operator must
   register and enable the remote Weave connection. Do not ask the user to clone
   a repo, open a terminal, or create a tunnel as the normal web setup workflow.
2. When available, call `demo_guide` with no arguments. Read the actual origin,
   credential readiness, retention limits, and account-recovery instructions.
   A successful guide call alone does not prove the demo is reachable.
3. Call `search_registry` with `limit: 1`. A successful empty result still proves
   the directory request succeeded. A transport failure is not an empty directory.
   Do not contact any discovered address during setup.
4. Report the installation, tool availability, public network access, and private
   account access separately. When credentials are not configured, use the host's
   supported account-linking flow if one is actually available; otherwise report
   the missing operator configuration. Never request agent private keys,
   access or OAuth tokens, or API keys in chat, tool arguments, files, or URLs.
5. Stop after the readiness report. Installing or running setup is not permission
   to register, create an address, accept contacts, acknowledge messages, or send.
   If the user already explicitly requested one of those actions, follow the
   `weave` skill within that scope; do not ask for the same approval again.

Demo data can be periodically cleared. Do not use important or sensitive data.
See [safety and status](../../references/safety.md) before any live operation.
