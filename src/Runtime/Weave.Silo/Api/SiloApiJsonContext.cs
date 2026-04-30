using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Weave.Agents.Models;
using Weave.Workspaces.Models;
using Weave.Workspaces.Plugins;

namespace Weave.Silo.Api;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StartWorkspaceRequest))]
[JsonSerializable(typeof(WorkspaceResponse))]
[JsonSerializable(typeof(AgentResponse))]
[JsonSerializable(typeof(AgentDefinition))]
[JsonSerializable(typeof(TaskResponse))]
[JsonSerializable(typeof(ToolConnectionResponse))]
[JsonSerializable(typeof(ToolConnectionStatus))]
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
[JsonSerializable(typeof(ChatResponse))]
[JsonSerializable(typeof(ConversationMessageResponse))]
[JsonSerializable(typeof(ConnectPluginRequest))]
[JsonSerializable(typeof(ConnectPluginResponse))]
[JsonSerializable(typeof(PluginStatus))]
[JsonSerializable(typeof(IEnumerable<WorkspaceResponse>))]
[JsonSerializable(typeof(IEnumerable<AgentResponse>))]
[JsonSerializable(typeof(IEnumerable<TaskResponse>))]
[JsonSerializable(typeof(IEnumerable<ToolConnectionResponse>))]
[JsonSerializable(typeof(IEnumerable<PluginStatus>))]
[JsonSerializable(typeof(IReadOnlyList<PluginStatus>))]
[JsonSerializable(typeof(PluginSchema))]
[JsonSerializable(typeof(PluginConfigField))]
[JsonSerializable(typeof(IEnumerable<PluginSchema>))]
[JsonSerializable(typeof(IReadOnlyList<PluginSchema>))]
// Skill types
[JsonSerializable(typeof(StoreSkillRequest))]
[JsonSerializable(typeof(SkillStepRequest))]
[JsonSerializable(typeof(SkillResponse))]
[JsonSerializable(typeof(SkillSearchResultResponse))]
[JsonSerializable(typeof(SkillSuggestionResponse))]
[JsonSerializable(typeof(IEnumerable<SkillResponse>))]
[JsonSerializable(typeof(IEnumerable<SkillSearchResultResponse>))]
[JsonSerializable(typeof(IEnumerable<SkillSuggestionResponse>))]
// Channel types
[JsonSerializable(typeof(RegisterChannelRequest))]
[JsonSerializable(typeof(InboundMessageRequest))]
[JsonSerializable(typeof(ChannelResponse))]
[JsonSerializable(typeof(OutboundMessageResponse))]
[JsonSerializable(typeof(IEnumerable<ChannelResponse>))]
// User types
[JsonSerializable(typeof(SetPreferenceRequest))]
[JsonSerializable(typeof(SetDomainContextRequest))]
[JsonSerializable(typeof(UserProfileResponse))]
// Marketplace types
[JsonSerializable(typeof(SubmitMarketplaceItemRequest))]
[JsonSerializable(typeof(PublishMarketplaceItemRequest))]
[JsonSerializable(typeof(RateMarketplaceItemRequest))]
[JsonSerializable(typeof(MarketplaceItemResponse))]
[JsonSerializable(typeof(IEnumerable<MarketplaceItemResponse>))]
// Template types
[JsonSerializable(typeof(RegisterTemplateRequest))]
[JsonSerializable(typeof(TemplateResponse))]
[JsonSerializable(typeof(TemplateValidationResultResponse))]
[JsonSerializable(typeof(IEnumerable<TemplateResponse>))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
internal sealed partial class SiloApiJsonContext : JsonSerializerContext;
