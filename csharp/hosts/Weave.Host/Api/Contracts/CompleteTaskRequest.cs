namespace Weave.Silo.Api;

public sealed record CompleteTaskRequest
{
    public required bool Success { get; init; }
    public required List<ProofItemRequest> Proof { get; init; }
}
