# Weave MVP PRD

## Product Summary

Weave is a manifest-driven AI workspace orchestrator for .NET. The MVP should let a developer create a workspace from a preset or interactive prompts, start it through a hosted control plane, inspect workspace and agent state, and generate deployment artifacts — all through a consistent `weave workspace` CLI surface.

## Problem Statement

Developers experimenting with AI agents often stitch together ad hoc scripts, tool processes, prompt files, and environment-specific deployment setup. That creates three recurring problems:

1. agent runtime behavior is hard to reproduce
2. tool access and secret handling are inconsistent
3. moving from local development to hosted deployment requires manual translation

The MVP should make an agent workspace portable, inspectable, and safer by centering everything on a single manifest and a hosted runtime.

## Target User

### Primary user

- platform-minded developer or technical founder building agent-based workflows

### Secondary user

- small engineering team validating a multi-agent development workflow before broader production hardening

## Jobs To Be Done

- create a workspace from a preset or interactive prompts without editing JSON by hand
- start and stop that workspace locally through a consistent control plane
- activate agents and tools from the manifest
- inspect workspace, agent, and tool state
- incrementally add agents, tools, and targets to an existing workspace
- generate deployment manifests for downstream environments

## MVP Goals

### Goal 1

A user can create and validate a workspace from the CLI using presets or interactive prompts.

### Goal 2

A user can start and stop a workspace through `Weave.Silo` and observe resulting state from the CLI and dashboard.

### Goal 3

A workspace can activate agents, register tools, and expose agent and tool status through HTTP APIs.

### Goal 4

The platform enforces basic security controls around tool access and secret leakage.

### Goal 5

The same workspace definition can generate deployment artifacts for common targets without changing the source manifest.

### Goal 6

The CLI follows a single consistent command pattern: `weave workspace <action> <name>`.

## MVP Non-Goals

The MVP does not need to include:

- remote agent registration or runner fleets
- advanced multi-tenant policy management
- production-grade autoscaling logic
- full workflow authoring UI
- built-in long-term memory beyond manifest-defined config hooks
- direct deployment execution to cloud targets

## Core User Experience

### CLI command pattern

All workspace commands follow the pattern `weave workspace <action> <name>`:

```text
weave workspace new <name>          Create a new workspace
weave workspace list                List your workspaces
weave workspace remove <name>       Remove a workspace

weave workspace up <name>           Start a workspace
weave workspace down <name>         Stop a workspace
weave workspace status <name>       See what is happening in a workspace

weave workspace add agent <name>    Add an assistant
weave workspace add tool <name>     Add a tool
weave workspace add target <name>   Add a deployment target

weave workspace show <name>         Show the current configuration
weave workspace validate <name>     Check that everything is set up correctly
weave workspace publish <name>      Generate files for deploying elsewhere
weave workspace presets             Browse ready-made workspace templates
```

### Presets

Built-in presets let a user skip interactive prompts entirely:

| Preset | What you get |
|--------|-------------|
| **starter** | One assistant, no tools — the simplest possible workspace. |
| **coding-assistant** | An assistant with git and file tools, ready for code tasks. |
| **research** | An assistant with web and document tools for gathering information. |
| **multi-agent** | A supervisor and worker assistants for more complex workflows. |

### Happy path

1. user installs the CLI
2. user runs `weave workspace new demo` and selects a preset (or answers interactive prompts)
3. user runs `weave workspace up demo`
4. `Weave.Silo` provisions the workspace, activates agents, and connects tools
5. user runs `weave workspace status demo` or opens the dashboard
6. user runs `weave workspace publish demo` to generate deployment artifacts
7. user runs `weave workspace down demo`

### Incremental configuration path

After workspace creation, users can add components individually:

1. user runs `weave workspace add agent demo --name reviewer`
2. user runs `weave workspace add tool demo --name web`
3. user runs `weave workspace add target demo --name production`
4. user runs `weave workspace validate demo`

Manual JSON editing is an advanced scenario — the CLI should handle all common configuration.

## Functional Requirements

### Workspace definition

- support `workspace.json` version `1.0`
- support workspace, agent, tool, target, and hook sections already modeled in `Weave.Workspaces`
- validate required fields and basic cross-references such as agent tool references

### Workspace creation

- provide built-in presets (`starter`, `coding-assistant`, `research`, `multi-agent`)
- support `--preset` flag to skip interactive prompts
- when no preset is specified, present interactive prompts for preset selection, model choice, tool selection, and permission configuration
- generate the `workspace.json` and supporting files without manual editing

### Workspace lifecycle

- expose HTTP endpoints to start, stop, and query workspace state
- persist workspace state through Orleans grains
- provision local runtime resources through the current `PodmanRuntime`
- record workspace identifiers locally for CLI follow-up commands

### Workspace mutation

- support `add agent`, `add tool`, and `add target` subcommands to modify an existing workspace
- update the workspace manifest file in place when adding components

### Agent lifecycle

- activate agents from manifest definitions
- track agent status, task activity, history, and connected tools
- expose agent state through HTTP endpoints
- support chat and task submission through the hosted API surface

### Tool lifecycle

- register tools per workspace
- resolve tools for agents through the registry grain
- support current connector classes: MCP, CLI, OpenAPI, and optional Dapr
- expose tool connection state through HTTP endpoints

### Security

- require capability tokens for tool access
- scan tool input and output for likely secret leakage
- substitute secrets through the secret proxy flow
- fail closed when token validation or leak scanning blocks an invocation

### Deployment output

- generate manifests for `docker-compose`, `kubernetes`, `nomad`, `fly-io`, and `github-actions`
- write generated artifacts to a user-selected output folder

### Operator visibility

- provide CLI status output for manifest-only and live workspace views
- provide a basic dashboard for workspace and agent inspection

## Non-Functional Requirements

- target `.NET 10` for runtime projects
- preserve `netstandard2.0` compatibility for `Weave.SourceGen`
- keep warnings as errors in the default build
- maintain test coverage in the existing test projects
- prefer source-generated and trimming-aware patterns already used in the repo

## Success Metrics

### Adoption metrics

- a new developer can create, validate, and start a sample workspace in under five minutes using a preset
- the CLI happy path completes without manual JSON editing or API calls

### Product metrics

- workspace start and stop succeed for the default sample flow on a clean local setup
- generated deployment artifacts are produced for all currently supported targets
- agent and tool status are queryable after startup
- all presets produce valid workspaces that pass `weave workspace validate`

### Quality metrics

- `dotnet build Weave.slnx` passes cleanly
- `dotnet test Weave.slnx` passes cleanly
- documentation matches shipped behavior closely enough that setup does not require source inspection

## Risks

- local runtime behavior is currently centered on Podman, which narrows the MVP environment assumptions
- some manifest concepts are broader than what the hosted runtime executes today
- Dapr support is conditional, so behavior differs between local environments
- dashboard depth may lag behind the API surface during early MVP work

## Open Questions

- should the MVP officially require Podman, or should Docker also be a supported local runtime target
- what sample provider configuration is expected for `IChatClient` in a first-run experience
- which dashboard pages are required for MVP completion versus post-MVP polish

## Recommended MVP Cut

The MVP should prioritize the hosted, workspace-centric path already reflected in the codebase:

- workspace creation from presets and interactive prompts
- manifest validation
- workspace start and stop via `Weave.Silo`
- agent activation and state inspection
- tool registration and guarded invocation
- incremental workspace mutation via `add` subcommands
- deployment manifest generation
- basic dashboard visibility

Anything beyond that should be treated as post-MVP roadmap unless it is required to make the above flow usable end to end.
