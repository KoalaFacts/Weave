namespace Weave.Workspaces.Models;

public sealed record WorkspaceConfig
{
    public IsolationLevel Isolation { get; init; } = IsolationLevel.Full;
    public NetworkConfig? Network { get; init; }
    public FilesystemConfig? Filesystem { get; init; }
    public SecretsConfig? Secrets { get; init; }
    public StorageConfig? Storage { get; init; }
}