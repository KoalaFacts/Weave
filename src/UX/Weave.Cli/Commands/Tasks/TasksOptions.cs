namespace Weave.Cli.Commands;

/// <summary>
/// Options for <c>weave tasks</c>: optional workspace name (the manifest the
/// caller wants), optional agent name (a key inside that manifest's
/// <c>agents</c> map). Both default to interactive prompts.
/// </summary>
internal sealed record TasksOptions(string? Workspace, string? Agent);
