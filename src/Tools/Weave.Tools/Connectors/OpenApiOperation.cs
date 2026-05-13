namespace Weave.Tools.Connectors;

internal sealed record OpenApiOperation
{
    public required string OperationId { get; init; }
    public required string HttpMethod { get; init; }
    public required string PathTemplate { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<OpenApiParameter> Parameters { get; init; } = [];
    public bool HasRequestBody { get; init; }
}

internal sealed record OpenApiParameter
{
    public required string Name { get; init; }
    public required string In { get; init; }
    public bool Required { get; init; }
}
