# Weave

Weave lets you set up AI assistants that can use tools on your behalf — with guardrails you control. Define what an assistant can do, which tools it can access, and how your data stays protected. Then run it all locally with one command.

## Why Weave

- **Get started in minutes** — install, pick a preset, and you have a working setup.
- **You stay in control** — decide exactly which tools your assistants can use and what they are allowed to do.
- **Your data stays yours** — secrets and sensitive information are handled with privacy and security first.
- **See what is happening** — a live dashboard shows what your assistants are working on at any time.
- **Reuse what you build** — save your setup once and reuse or share it without starting over.
- **Grow at your own pace** — everything works locally out of the box. Add more capabilities only when you need them.

## What People Use It For

- **Automate tasks safely** — let an assistant run approved tools (like checking code, searching files, or calling services) without giving it free access to everything.
- **Keep things private** — passwords, keys, and sensitive settings stay protected and never leak into places they should not be.
- **Work solo or with a team** — use the same setup whether you are experimenting on your own or sharing it across a group.
- **See everything in one place** — a dashboard shows you what assistants are doing, what tasks are running, and what has finished.
- **Try things without risk** — save any setup as a reusable template so you can rerun it or hand it to someone else without redoing the work.

## Quick Start

Weave runs on Windows, macOS, and Linux. One command to install, one command to start.

**Install** — pick the one that matches your system:

| Platform | Command |
|----------|--------|
| **Windows** | `irm https://raw.githubusercontent.com/KoalaFacts/Weave/main/scripts/install.ps1 \| iex` |
| **macOS / Linux** | `curl -fsSL https://raw.githubusercontent.com/KoalaFacts/Weave/main/scripts/install.sh \| sh` |
| **.NET** | `dotnet tool install --global Weave.Cli` |

Or download directly from [GitHub Releases](https://github.com/KoalaFacts/Weave/releases).

**Create and run your first workspace:**

```bash
weave workspace new demo
weave workspace up demo
```

That is it. Everything runs locally on your machine by default — no extra services, no complicated setup. When you are ready for more, you can add advanced capabilities at your own pace.

## Configuring a Workspace

The fastest way to configure a workspace is through the CLI. Answer a few prompts or pick a preset and you are done.

### Start from a preset

```bash
# See available presets
weave workspace presets

# Create a workspace from a preset
weave workspace new demo --preset coding-assistant
```

Built-in presets get you running in seconds:

| Preset | What you get |
|--------|-------------|
| **starter** | One assistant, no tools — the simplest possible workspace. |
| **coding-assistant** | An assistant with git and file tools, ready for code tasks. |
| **research** | An assistant with web and document tools for gathering information. |
| **multi-agent** | A supervisor and worker assistants for more complex workflows. |

### Build interactively

No flags to memorize. The CLI gives you a rich interactive terminal where you select options, pick from lists, and confirm as you go:

```bash
weave workspace new demo
```

```
? Choose a preset:
  ❯ starter
    coding-assistant
    research
    multi-agent
    custom (configure everything yourself)

? Select a model for your assistant:
  ❯ claude-sonnet-4-20250514
    gpt-4o
    custom...

? Which tools should the assistant have access to?
  ❯ ◉ git
    ◉ file
    ◯ web
    ◯ custom...

? What should the assistant be allowed to do?
  ❯ ◉ use all available tools
    ◯ read-only access
    ◯ custom...

✔ Workspace "demo" created. Run `weave workspace up demo` to start.
```

You can also add or change things individually at any time:

```bash
weave workspace add agent demo --name reviewer
weave workspace add tool demo --name web
weave workspace add target demo --name production
weave workspace show demo
weave workspace validate demo
```

Every command updates the workspace configuration for you. No manual file editing required.

### What you can configure

| Section | What it controls |
|---------|------------------|
| **workspace** | Privacy, security boundaries, and how secrets are managed. |
| **agents** | Which AI model to use, what it can do, and any recurring tasks. |
| **tools** | Which tools (commands, services, APIs) the assistant can call. |
| **hooks** | Actions that run automatically at key moments (e.g. on start, on finish). |
| **targets** | Where the workspace runs — your machine, a server, or a CI pipeline. |

For advanced scenarios you can also edit the configuration file by hand. See the [Manifest Reference](docs/manifest-reference.md) for details.

## CLI Commands

The CLI is designed for a rich terminal experience — clear output, interactive prompts, and no guesswork.

```text
weave workspace new <name>          Create a new workspace
weave workspace list                List your workspaces
weave workspace remove <name>       Remove a workspace

weave workspace up <name>           Start a workspace
weave workspace down <name>         Stop a workspace
weave workspace status <name>       See what is happening in a workspace

weave workspace add agent <name>    Add an assistant
weave workspace add tool <name>     Add a tool
weave workspace add target <name>   Add a place to run the workspace

weave workspace show <name>         Show the current configuration
weave workspace validate <name>     Check that everything is set up correctly
weave workspace publish <name>      Generate files for deploying elsewhere
weave workspace presets             Browse ready-made workspace templates
```

## License

MIT or Apache 2.0.
