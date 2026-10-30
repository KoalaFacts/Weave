namespace Weave.Cli.Commands;

internal sealed record WorkspaceStorageConflictResolution(bool Abort, string Database, string ConnectionString);
