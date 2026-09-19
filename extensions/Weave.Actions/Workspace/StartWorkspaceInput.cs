using Weave.Workspaces.Manifest;

namespace Weave.Actions.Workspace;

/// <summary>
/// Input for <see cref="StartWorkspaceAction"/>. The frontend is responsible
/// for reading and parsing the manifest from disk and resolving any
/// relative <c>SystemPromptFile</c> paths to absolute (the silo runs in a
/// separate process and sees no relative paths). The action then POSTs the
/// prepared manifest as-is.
/// </summary>
public sealed record StartWorkspaceInput(WorkspaceManifest Manifest);
