using System.Text.Json.Serialization;

namespace Weave.Dashboard.Services;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WorkspaceDto))]
[JsonSerializable(typeof(AgentDto))]
[JsonSerializable(typeof(TaskDto))]
[JsonSerializable(typeof(AgentChatResponseDto))]
[JsonSerializable(typeof(ConversationMessageDto))]
[JsonSerializable(typeof(ToolConnectionDto))]
[JsonSerializable(typeof(SendMessageDto))]
[JsonSerializable(typeof(List<WorkspaceDto>))]
[JsonSerializable(typeof(List<AgentDto>))]
[JsonSerializable(typeof(List<ToolConnectionDto>))]
public sealed partial class DashboardJsonContext : JsonSerializerContext;
