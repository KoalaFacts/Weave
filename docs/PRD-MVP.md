# Weave MVP PRD

## Product Summary

Weave is a manifest-driven AI workspace orchestrator for .NET. The MVP should let a developer define a workspace in `workspace.yml`, start it through a hosted control plane, inspect workspace and agent state, and generate deployment artifacts from the same source definition.

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

- define a workspace once in a manifest
- start and stop that workspace locally through a consistent control plane
- activate agents and tools from the manifest
- inspect workspace, agent, and tool state
- generate deployment manifests for downstream environments

## MVP Goals

### Goal 1

A user can create and validate a workspace manifest from the CLI.

### Goal 2

A user can start and stop a workspace through `Weave.Silo` and observe resulting state from the CLI and dashboard.

### Goal 3

A workspace can activate agents, register tools, and expose agent and tool status through HTTP APIs.

### Goal 4

The platform enforces basic security controls around tool access and secret leakage.

### Goal 5

The same workspace definition can generate deployment artifacts for common targets without changing the source manifest.

## MVP Non-Goals

The MVP does not need to include:

- remote agent registration or runner fleets
- advanced multi-tenant policy management
- production-grade autoscaling logic
- full workflow authoring UI
- built-in long-term memory beyond manifest-defined config hooks
- direct deployment execution to cloud targets

## Core User Experience

### Happy path

1. user starts `Weave.AppHost`
2. user runs `weave workspace new demo`
3. user edits `workspaces/demo/workspace.yml`
4. user runs `weave config validate --workspace demo`
5. user runs `weave up --workspace demo`
6. `Weave.Silo` provisions the workspace, activates agents, and connects tools
7. user runs `weave status --workspace demo` or opens the dashboard
8. user runs `weave publish kubernetes --workspace demo`
9. user runs `weave down --workspace demo`

## Functional Requirements

### Workspace definition

- support `workspace.yml` version `1.0`
- support workspace, agent, tool, target, and hook sections already modeled in `Weave.Workspaces`
- validate required fields and basic cross-references such as agent tool references

### Workspace lifecycle

- expose HTTP endpoints to start, stop, and query workspace state
- persist workspace state through Orleans grains
- provision local runtime resources through the current `PodmanRuntime`
- record workspace identifiers locally for CLI follow-up commands

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

- a new developer can create, validate, and start a sample workspace in under 15 minutes
- the CLI happy path completes without manual API calls

### Product metrics

- workspace start and stop succeed for the default sample flow on a clean local setup
- generated deployment artifacts are produced for all currently supported targets
- agent and tool status are queryable after startup

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
- should agent chat and task submission get first-class CLI commands during the MVP
- which dashboard pages are required for MVP completion versus post-MVP polish

## Recommended MVP Cut

The MVP should prioritize the hosted, workspace-centric path already reflected in the codebase:

- manifest creation and validation
- workspace start and stop via `Weave.Silo`
- agent activation and state inspection
- tool registration and guarded invocation
- deployment manifest generation
- basic dashboard visibility

Anything beyond that should be treated as post-MVP roadmap unless it is required to make the above flow usable end to end.
