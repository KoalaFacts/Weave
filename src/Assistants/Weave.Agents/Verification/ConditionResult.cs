namespace Weave.Agents.Models;

public sealed record ConditionResult
{
    public required string ConditionName { get; init; }
    public required bool Passed { get; init; }
    public string? Detail { get; init; }
}