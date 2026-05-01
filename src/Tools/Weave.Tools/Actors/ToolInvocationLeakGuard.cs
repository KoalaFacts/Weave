using System.Text.Json;
using Microsoft.Extensions.Logging;
using Weave.Security.Scanning;
using Weave.Shared.Events;
using Weave.Shared.Ids;
using Weave.Tools.Events;
using Weave.Tools.Models;

namespace Weave.Tools.Actors;

internal sealed partial class ToolInvocationLeakGuard(
    ILeakScanner leakScanner,
    IEventBus eventBus,
    ILogger<ToolActor> logger)
{
    public async Task<ToolResult?> BlockIfOutboundLeaksAsync(
        string workspaceId,
        string toolName,
        ToolInvocation invocation)
    {
        var outboundPayload = invocation.RawInput ?? JsonSerializer.Serialize(invocation.Parameters, ToolJsonContext.Default.DictionaryStringString);
        if (string.IsNullOrWhiteSpace(outboundPayload))
            return null;

        var scanResult = await leakScanner.ScanStringAsync(outboundPayload, new ScanContext
        {
            WorkspaceId = workspaceId,
            SourceComponent = $"tool:{toolName}",
            Direction = ScanDirection.Outbound
        });

        if (!scanResult.HasLeaks)
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
        if (!result.Success || string.IsNullOrEmpty(result.Output))
            return result;

        var responseScan = await leakScanner.ScanStringAsync(result.Output, new ScanContext
        {
            WorkspaceId = workspaceId,
            SourceComponent = $"tool:{toolName}",
            Direction = ScanDirection.Inbound
        });

        if (!responseScan.HasLeaks)
            return result;

        LogLeakDetectedRedacted(toolName);
        await eventBus.PublishAsync(new ToolInvocationBlockedEvent
        {
            SourceId = $"{workspaceId}/{toolName}",
            ToolName = toolName,
            WorkspaceId = WorkspaceId.From(workspaceId),
            Reason = "Secret leak detected in inbound response"
        }, CancellationToken.None);

        return result with { Output = "***REDACTED: potential secret detected in response***" };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Secret leak detected in tool invocation for '{Tool}' - blocked")]
    private partial void LogLeakDetectedBlocked(string tool);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Secret leak detected in tool response from '{Tool}' - redacted")]
    private partial void LogLeakDetectedRedacted(string tool);
}
