namespace Weave.Workspaces.Manifest;

public sealed record CliConfig
{
    public string Shell { get; init; } = "/bin/bash";
    public IReadOnlyList<string> AllowedCommands { get; init; } = [];
    public IReadOnlyList<string> DeniedCommands { get; init; } = [];
}
