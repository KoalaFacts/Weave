using System.Text.Json.Serialization;
using Weave.Tools.Tool;

namespace Weave.Security.Sqlite;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record StoredProposalInput
{
    public string InvocationId { get; init; } = string.Empty;
    public string ToolName { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public Dictionary<string, string> Parameters { get; init; } = [];
    public string? RawInput { get; init; }

    public static StoredProposalInput FromInvocation(ToolInvocation request) => new()
    {
        InvocationId = request.InvocationId!.Value.ToString(),
        ToolName = request.ToolName,
        Method = request.Method,
        Parameters = new(request.Parameters, StringComparer.Ordinal),
        RawInput = request.RawInput
    };

    public ToolInvocation ToInvocation() => new()
    {
        InvocationId = Weave.Invocations.InvocationId.From(InvocationId),
        ToolName = ToolName,
        Method = Method,
        Parameters = Parameters,
        RawInput = RawInput
    };
}
