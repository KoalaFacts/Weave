using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Weave.Agents.Pipeline;

public interface IAgentChatClientFactory
{
    IChatClient Create(string agentId, string? modelId = null);
}

public sealed class AgentChatClientFactory(
    IServiceProvider services,
    IAgentCostLedger costLedger,
    ILoggerFactory loggerFactory) : IAgentChatClientFactory
{
    public IChatClient Create(string agentId, string? modelId = null)
    {
        var baseClient = ActivatorUtilities.CreateInstance<FallbackChatClient>(services, [(modelId!)]);
        var rateLimited = new RateLimitingChatClient(
            baseClient,
            maxRequestsPerMinute: 60,
            loggerFactory.CreateLogger<RateLimitingChatClient>());
        var tracked = new CostTrackingChatClient(
            rateLimited,
            costLedger,
            loggerFactory.CreateLogger<CostTrackingChatClient>());

        var builder = new ChatClientBuilder(tracked)
            .UseFunctionInvocation(loggerFactory);

        return builder.Build(services);
    }
}

internal sealed partial class FallbackChatClient(string? defaultModelId, ILogger<FallbackChatClient> logger) : IChatClient
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ChatClientMetadata _metadata = new("weave-fallback", new Uri("https://weave.local/"), defaultModelId ?? "weave-local");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        var lastFunctionResult = FindLastFunctionResult(messageList);
        ChatResponse response;

        if (lastFunctionResult is not null)
        {
            response = new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                $"Tool result: {lastFunctionResult.Result}"))
            {
                ModelId = options?.ModelId ?? _metadata.DefaultModelId,
                Usage = CreateUsage(messageList, $"Tool result: {lastFunctionResult.Result}")
            };
        }
        else
        {
            var userText = messageList.LastOrDefault(static m => m.Role == ChatRole.User)?.Text ?? string.Empty;
            if (TryCreateFunctionCall(userText, options, out var functionMessage))
            {
                response = new ChatResponse(functionMessage)
                {
                    ModelId = options?.ModelId ?? _metadata.DefaultModelId,
                    Usage = CreateUsage(messageList, userText)
                };
            }
            else
            {
                var text = string.IsNullOrWhiteSpace(userText)
                    ? "No input provided."
                    : $"[{options?.ModelId ?? _metadata.DefaultModelId}] {userText}";
                response = new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
                {
                    ModelId = options?.ModelId ?? _metadata.DefaultModelId,
                    Usage = CreateUsage(messageList, text)
                };
            }
        }

        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        LogStreamingNotSupported(response.ModelId);
        yield break;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? _metadata : null;

    public void Dispose() { }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fallback chat client does not support incremental streaming; returning no updates for {ModelId}")]
    private partial void LogStreamingNotSupported(string? modelId);

    private static FunctionResultContent? FindLastFunctionResult(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages.Reverse())
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionResultContent result)
                    return result;
            }
        }

        return null;
    }

    private static bool TryCreateFunctionCall(string userText, ChatOptions? options, out ChatMessage message)
    {
        const string Prefix = "/tool ";
        if (!userText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            message = default!;
            return false;
        }

        var payload = userText[Prefix.Length..].Trim();
        var firstSpace = payload.IndexOf(' ');
        var toolName = firstSpace >= 0 ? payload[..firstSpace] : payload;
        var input = firstSpace >= 0 ? payload[(firstSpace + 1)..].Trim() : string.Empty;

        if (options?.Tools is not { Count: > 0 } tools || !tools.Any(t => string.Equals(t.Name, toolName, StringComparison.Ordinal)))
        {
            message = new ChatMessage(ChatRole.Assistant, $"Tool '{toolName}' is not available.");
            return true;
        }

        Dictionary<string, object?> args = new(StringComparer.Ordinal)
        {
            ["input"] = input
        };

        if (TryParseStructuredToolInput(input, out var method, out var structuredArgs))
        {
            args = structuredArgs.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.Ordinal);
            args["input"] = input;
            args["method"] = method;
        }

        message = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), toolName, args)]);
        return true;
    }

    private static bool TryParseStructuredToolInput(
        string input,
        out string method,
        out Dictionary<string, object> arguments)
    {
        method = "invoke";
        arguments = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["method"] = method,
            ["input"] = input
        };

        if (string.IsNullOrWhiteSpace(input) || !input.TrimStart().StartsWith('{'))
            return false;

        try
        {
            using var document = JsonDocument.Parse(input);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
                return false;

            arguments = new(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                arguments[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number when property.Value.TryGetInt64(out var l) => l,
                    JsonValueKind.Number => property.Value.GetDouble(),
                    JsonValueKind.Object or JsonValueKind.Array => property.Value.Deserialize<object>(_jsonOptions) ?? string.Empty,
                    JsonValueKind.Null or JsonValueKind.Undefined => null!,
                    _ => property.Value.Deserialize<object>(_jsonOptions) ?? string.Empty
                };
            }

            if (arguments.TryGetValue("method", out var methodValue) && methodValue is not null)
                method = methodValue.ToString() ?? "invoke";

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static UsageDetails CreateUsage(IEnumerable<ChatMessage> messages, string outputText)
    {
        var inputTokens = 0;
        foreach (var message in messages)
        {
            inputTokens += CountPseudoTokens(message.Text);
        }

        return new UsageDetails
        {
            InputTokenCount = inputTokens,
            OutputTokenCount = CountPseudoTokens(outputText)
        };
    }

    private static int CountPseudoTokens(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }
}
