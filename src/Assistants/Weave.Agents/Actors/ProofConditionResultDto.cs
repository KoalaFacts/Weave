namespace Weave.Agents.Actors;

internal sealed record ProofConditionResultDto
{
    public string? ConditionName { get; init; }
    public bool Passed { get; init; }
    public string? Detail { get; init; }
}