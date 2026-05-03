namespace Weave.Tools.Models;

public sealed record SecurityReview
{
    public required string ReviewerId { get; init; }
    public required bool Approved { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset ReviewedAt { get; init; } = DateTimeOffset.UtcNow;
}
