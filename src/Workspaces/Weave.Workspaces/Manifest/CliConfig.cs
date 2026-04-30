namespace Weave.Workspaces.Models;

public sealed record CliConfig
{
    public string Shell { get; init; } = "/bin/bash";
    public List<string> AllowedCommands { get; init; } = [];
    public List<string> DeniedCommands { get; init; } = [];
}
