using System.Text.Json.Serialization;
using Weave.Agents.ToolRegistry;
using Weave.Tools.Tool;
namespace Weave.Silo.Api;

public sealed record ToolConnectionResponse
{
    public required string ToolName { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<ToolType>))]
    public required ToolType ToolType { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<ToolConnectionStatus>))]
    public required ToolConnectionStatus Status { get; init; }
    public string? Endpoint { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }
    public string? ErrorMessage { get; init; }

    public static ToolConnectionResponse FromConnection(ToolConnection conn) => new()
    {
        ToolName = conn.ToolName,
        ToolType = Enum.TryParse<ToolType>(conn.ToolType, ignoreCase: true, out var toolType) ? toolType : ToolType.Mcp,
        Status = conn.Status,
        Endpoint = conn.Endpoint,
        ConnectedAt = conn.ConnectedAt,
        ErrorMessage = conn.ErrorMessage
    };
}
