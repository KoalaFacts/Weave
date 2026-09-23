using System.Text.Json;
using Microsoft.Extensions.Logging;
using Weave.Security.Scanning;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Tools.Marketplace;
namespace Weave.Tools.Tool;

internal sealed partial class ToolInvocationLeakGuard(
    ILeakScanner leakScanner,
    IEventBus eventBus,
    ILogger<ToolActor> logger)
{
    private const string RedactedResponse = "***REDACTED: potential secret detected in response***";

    public async Task<ToolResult?> BlockIfOutboundLeaksAsync(
        string workspaceId,
        string toolName,
        ToolInvocation invocation)
    {
        var context = new ScanContext
        {
            WorkspaceId = workspaceId,
            SourceComponent = $"tool:{toolName}",
            Direction = ScanDirection.Outbound
        };

        if (!await HasLeaksAsync(invocation.RawInput, context)
            && !await HasLeaksAsync(JsonSerializer.Serialize(invocation.Parameters, ToolJsonContext.Default.DictionaryStringString), context))
            return null;

        LogLeakDetectedBlocked(toolName);
        await eventBus.PublishAsync(new ToolInvocationBlockedEvent
        {
            SourceId = $"{workspaceId}/{toolName}",
            ToolName = toolName,
            WorkspaceId = WorkspaceId.From(workspaceId),
            Reason = "Secret leak detected in outbound payload"
        }, CancellationToken.None);

        return new ToolResult
        {
            Success = false,
            ToolName = toolName,
            Error = "Tool invocation blocked: potential secret leak detected in payload"
        };
    }

    public async Task<ToolResult> RedactIfInboundLeaksAsync(
        string workspaceId,
        string toolName,
        ToolResult result)
    {
        var context = new ScanContext
        {
            WorkspaceId = workspaceId,
            SourceComponent = $"tool:{toolName}",
            Direction = ScanDirection.Inbound
        };
        var outputLeaks = await HasLeaksAsync(result.Output, context);
        var errorLeaks = await HasLeaksAsync(result.Error, context);
        if (!outputLeaks && !errorLeaks)
            return result;

        LogLeakDetectedRedacted(toolName);
        await eventBus.PublishAsync(new ToolInvocationBlockedEvent
        {
            SourceId = $"{workspaceId}/{toolName}",
            ToolName = toolName,
            WorkspaceId = WorkspaceId.From(workspaceId),
            Reason = "Secret leak detected in inbound response"
        }, CancellationToken.None);

        return result with
        {
            Output = outputLeaks ? RedactedResponse : result.Output,
            Error = errorLeaks ? RedactedResponse : result.Error
        };
    }

    private async Task<bool> HasLeaksAsync(string? payload, ScanContext context)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        return (await leakScanner.ScanStringAsync(payload, context)).HasLeaks;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Secret leak detected in tool invocation for '{Tool}' - blocked")]
    private partial void LogLeakDetectedBlocked(string tool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Secret leak detected in tool response from '{Tool}' - redacted")]
    private partial void LogLeakDetectedRedacted(string tool);
}
