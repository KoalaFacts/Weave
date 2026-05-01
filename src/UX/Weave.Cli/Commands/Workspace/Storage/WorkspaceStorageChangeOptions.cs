namespace Weave.Cli.Commands;

internal sealed record WorkspaceStorageChangeOptions(
    string Workspace,
    string? Backend,
    string? ConnectionString,
    string? Schema,
    string? Database,
    string? Isolation);
