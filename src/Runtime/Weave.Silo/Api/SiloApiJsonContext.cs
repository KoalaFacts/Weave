using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Weave.Workspaces.Plugins;

namespace Weave.Silo.Api;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StartWorkspaceRequest))]
[JsonSerializable(typeof(WorkspaceResponse))]
[JsonSerializable(typeof(AgentResponse))]
[JsonSerializable(typeof(TaskResponse))]
[JsonSerializable(typeof(ToolConnectionResponse))]
[JsonSerializable(typeof(ActivateAgentRequest))]
[JsonSerializable(typeof(SubmitTaskRequest))]
[JsonSerializable(typeof(SendMessageRequest))]
[JsonSerializable(typeof(CompleteTaskRequest))]
[JsonSerializable(typeof(ReviewTaskRequest))]
[JsonSerializable(typeof(ProofItemRequest))]
[JsonSerializable(typeof(ProofOfWorkResponse))]
[JsonSerializable(typeof(ProofItemResponse))]
[JsonSerializable(typeof(VerificationRecordResponse))]
[JsonSerializable(typeof(VerificationVoteResponse))]
[JsonSerializable(typeof(ConditionResultResponse))]
[JsonSerializable(typeof(AgentChatResponse))]
[JsonSerializable(typeof(ConversationMessageResponse))]
[JsonSerializable(typeof(ConnectPluginRequest))]
[JsonSerializable(typeof(ConnectPluginResponse))]
[JsonSerializable(typeof(PluginStatus))]
[JsonSerializable(typeof(IEnumerable<WorkspaceResponse>))]
[JsonSerializable(typeof(IEnumerable<AgentResponse>))]
[JsonSerializable(typeof(IEnumerable<ToolConnectionResponse>))]
[JsonSerializable(typeof(IEnumerable<PluginStatus>))]
[JsonSerializable(typeof(IReadOnlyList<PluginStatus>))]
[JsonSerializable(typeof(PluginSchema))]
[JsonSerializable(typeof(PluginConfigField))]
[JsonSerializable(typeof(IEnumerable<PluginSchema>))]
[JsonSerializable(typeof(IReadOnlyList<PluginSchema>))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
internal sealed partial class SiloApiJsonContext : JsonSerializerContext;
