namespace Weave.Cli.Shell;

internal sealed record UpdateCache
{
    public required string LatestVersion { get; init; }
    public required DateTimeOffset CheckedAt { get; init; }
}
