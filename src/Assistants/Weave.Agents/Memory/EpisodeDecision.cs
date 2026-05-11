namespace Weave.Agents.Memory;

public sealed record EpisodeDecision
{
    public required string Question { get; init; }
    public required string ChosenOption { get; init; }
}
