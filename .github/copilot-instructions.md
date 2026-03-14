# Copilot Instructions

## Project Guidelines
- Do not use FluentAssertions as it has a commercial license. Use Shouldly instead for test assertions.
- Emphasize permissive licensing (MIT or Apache 2.0) — freedom for users matters.

## README and Product-Facing Documentation
- Write for a non-technical audience first. Avoid developer jargon, internal project names, and implementation details.
- Lead with user value: what the product does for people, not how it is built.
- Privacy, security, and user control are the primary value proposition. Always position these first.
- Do not include tech-stack tables, architecture diagrams, or project-structure listings. Those belong in separate developer docs if needed.
- Present advanced components (Dapr, container runtimes, external secret providers, etc.) as optional opt-ins, not defaults.
- The default local setup is in-process — no external services required. But local is also fully composable; users can swap in advanced components whenever they want.
- The project is cross-platform (Windows, macOS, Linux). The CLI installer should be presented as stupidly easy — one command per platform.
- The CLI emphasizes a rich interactive terminal experience: selection prompts, multi-select, confirmations — not flags to memorize.
- Include presets for first-time users so they can start without configuring anything.
- CLI commands follow a consistent pattern: `weave workspace <action> <name>`.
- JSON manifest editing is an advanced scenario. Link to a separate reference doc instead of inlining full examples.
- Use language like "assistants" and "tools" rather than "agents" and "grains" in user-facing copy.
- GitHub repo: https://github.com/KoalaFacts/Weave
