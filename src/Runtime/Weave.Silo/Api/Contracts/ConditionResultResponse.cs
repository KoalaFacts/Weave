using Weave.Agents.Verification;
namespace Weave.Silo.Api;

public sealed record ConditionResultResponse
{
    public required string ConditionName { get; init; }
    public required bool Passed { get; init; }
    public string? Detail { get; init; }

    public static ConditionResultResponse FromResult(ConditionResult result) => new()
    {
        ConditionName = result.ConditionName,
        Passed = result.Passed,
        Detail = result.Detail
    };
}
