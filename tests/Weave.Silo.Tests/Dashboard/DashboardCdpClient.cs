using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Weave.Silo.Tests.Dashboard;

internal sealed class DashboardCdpClient(Uri dashboardOrigin, Func<string> childDiagnostics) : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private Task? _receiving;
    private int _sequence;

    public ConcurrentQueue<string> BlockedRequests { get; } = new();

    public async Task ConnectAsync(Uri endpoint)
    {
        endpoint.IsLoopback.ShouldBeTrue("The test browser debugging endpoint must be loopback.");
        dashboardOrigin.IsLoopback.ShouldBeTrue("Only the owned Dashboard origin may be requested by the browser.");
        _socket.Options.Proxy = null;
        using var deadline = Deadline();
        await _socket.ConnectAsync(endpoint, deadline.Token);
        _receiving = ReceiveAsync();
        await CommandAsync("Page.enable");
        await CommandAsync("Runtime.enable");
        await CommandAsync("Fetch.enable", new JsonObject
        {
            ["patterns"] = new JsonArray(new JsonObject { ["urlPattern"] = "*", ["requestStage"] = "Request" })
        });
    }

    public async Task<JsonElement> CommandAsync(string method, JsonObject? parameters = null)
    {
        var id = Interlocked.Increment(ref _sequence);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        using var deadline = Deadline();
        try
        {
            await WriteAsync(id, method, parameters, deadline.Token);
            var message = await reply.Task.WaitAsync(deadline.Token);
            if (message.TryGetProperty("error", out var error))
                throw new InvalidOperationException("Chrome rejected " + method + ": " + error.GetRawText());
            return message.GetProperty("result");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task<T> EvaluateAsync<T>(string expression)
    {
        var result = await CommandAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
            ["awaitPromise"] = true
        });
        if (result.TryGetProperty("exceptionDetails", out var error))
            throw new InvalidOperationException("Browser expression failed: " + error.GetRawText());
        var value = result.GetProperty("result").GetProperty("value");
        object primitive = value.ValueKind switch
        {
            JsonValueKind.True or JsonValueKind.False => value.GetBoolean(),
            JsonValueKind.String => value.GetString().ShouldNotBeNull(),
            JsonValueKind.Number => value.GetInt32(),
            _ => throw new InvalidDataException("Browser expression did not return a bool, string, or integer.")
        };
        return (T)primitive;
    }

    public Task WaitForAsync(string expression, string reason) =>
        WaitForAsync(() => EvaluateAsync<bool>(expression), reason);

    public Task WaitForFileInputAsync() => WaitForAsync(FileInputHasChangeListenerAsync, "interactive file input change listener");

    private async Task<bool> FileInputHasChangeListenerAsync()
    {
        var evaluated = await CommandAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = "document.querySelector('#review-file')",
            ["returnByValue"] = false
        });
        var element = evaluated.GetProperty("result");
        if (!element.TryGetProperty("objectId", out var remoteId))
            return false;
        var objectId = remoteId.GetString().ShouldNotBeNull();
        Exception? operationError = null;
        var hasChangeListener = false;
        try
        {
            // Inspect actual DOM listeners, without accessing Blazor's private state or calling handlers.
            var listeners = await CommandAsync("DOMDebugger.getEventListeners", new JsonObject { ["objectId"] = objectId });
            hasChangeListener = listeners.GetProperty("listeners").EnumerateArray()
                .Any(listener => listener.GetProperty("type").GetString() == "change");
        }
        catch (Exception error)
        {
            operationError = error;
        }
        try
        {
            await CommandAsync("Runtime.releaseObject", new JsonObject { ["objectId"] = objectId });
        }
        catch (Exception releaseError) when (operationError is not null)
        {
            throw new AggregateException("File input listener inspection and remote object release both failed.",
                operationError, releaseError);
        }
        if (operationError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(operationError).Throw();
        return hasChangeListener;
    }

    private async Task WaitForAsync(Func<Task<bool>> predicate, string reason)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (!await predicate())
                await Task.Delay(50, deadline.Token);
        }
        catch (OperationCanceledException error) when (!TestContext.Current.CancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("Dashboard browser did not reach: " + reason + "\n" + await TimeoutDiagnosticsAsync(), error);
        }
    }

    private async Task<string> TimeoutDiagnosticsAsync()
    {
        string dom;
        try
        {
            // No credential values. Bound the browser reply as well as the captured child log tails.
            dom = await EvaluateAsync<string>("""
                JSON.stringify({
                  page: document.querySelector('.review-page')?.innerText.slice(0, 4096),
                  fileInput: !!document.querySelector('#review-file'),
                  files: Array.from(document.querySelector('#review-file')?.files ?? []).slice(0, 2)
                    .map(file => ({ name: file.name.slice(0, 128), size: file.size })),
                  buttons: Array.from(document.querySelectorAll('.review-page .actions button')).slice(0, 4)
                    .map(button => ({ text: button.textContent.slice(0, 128), disabled: button.disabled })),
                  blazorErrorVisible: !!document.querySelector('#blazor-error-ui')?.getClientRects().length
                })
                """);
        }
        catch (Exception error)
        {
            // This is the final diagnostic boundary: parsing/protocol failures must not replace the timeout.
            dom = "DOM diagnostics unavailable: " + error.GetType().Name + ": " + error.Message;
        }
        string children;
        try
        {
            children = childDiagnostics();
        }
        catch (Exception error)
        {
            children = "Child diagnostics unavailable: " + error.GetType().Name + ": " + error.Message;
        }
        return "DOM: " + Tail(dom, 8192) + "\n" + Tail(children, 8448);
    }

    internal static string Tail(string text, int limit) => text.Length <= limit ? text : text[^limit..];

    public async Task UploadAsync(string path)
    {
        var document = await CommandAsync("DOM.getDocument");
        var node = await CommandAsync("DOM.querySelector", new JsonObject
        {
            ["nodeId"] = document.GetProperty("root").GetProperty("nodeId").GetInt32(),
            ["selector"] = "#review-file"
        });
        var nodeId = node.GetProperty("nodeId").GetInt32();
        nodeId.ShouldBeGreaterThan(0, "The real Review file input must exist.");
        await CommandAsync("DOM.setFileInputFiles", new JsonObject
        {
            ["nodeId"] = nodeId,
            ["files"] = new JsonArray(JsonValue.Create(path))
        });
    }

    private async Task WriteAsync(int id, string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var message = new JsonObject
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters?.DeepClone() ?? new JsonObject()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await _sending.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sending.Release();
        }
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[8192];
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                using var body = new MemoryStream();
                ValueWebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(buffer.AsMemory(), _lifetime.Token);
                    if (received.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException("Chrome closed its debugging connection.");
                    body.Write(buffer, 0, received.Count);
                    if (body.Length > 1_048_576)
                        throw new InvalidDataException("Chrome debugging response exceeded 1 MiB.");
                } while (!received.EndOfMessage);
                using var document = JsonDocument.Parse(body.GetBuffer().AsMemory(0, checked((int)body.Length)));
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var id) && _pending.TryGetValue(id.GetInt32(), out var pending))
                    pending.TrySetResult(root.Clone());
                else if (root.TryGetProperty("method", out var method) && method.GetString() == "Fetch.requestPaused")
                    await ContinueDashboardRequestAsync(root.GetProperty("params"));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Fixture disposal closes the protocol pump.
        }
        catch (WebSocketException) when (_lifetime.IsCancellationRequested)
        {
            // Abort unblocks the socket during fixture disposal.
        }
        catch (Exception error) when (error is WebSocketException or IOException or JsonException)
        {
            foreach (var pending in _pending.Values)
                pending.TrySetException(error);
            throw;
        }
    }

    private async Task ContinueDashboardRequestAsync(JsonElement parameters)
    {
        var address = parameters.GetProperty("request").GetProperty("url").GetString().ShouldNotBeNull();
        var permitted = Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && uri.Scheme == dashboardOrigin.Scheme && uri.Host == dashboardOrigin.Host && uri.Port == dashboardOrigin.Port;
        if (!permitted)
            BlockedRequests.Enqueue(address);
        var command = new JsonObject { ["requestId"] = parameters.GetProperty("requestId").GetString() };
        if (!permitted)
            command["errorReason"] = "BlockedByClient";
        // A protocol event response must not wait for another message from this receive loop.
        await WriteAsync(Interlocked.Increment(ref _sequence), permitted ? "Fetch.continueRequest" : "Fetch.failRequest",
            command, _lifetime.Token);
    }

    private CancellationTokenSource Deadline()
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        return deadline;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _socket.Abort();
        try
        {
            if (_receiving is not null)
                await _receiving.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            _socket.Dispose();
            _sending.Dispose();
            _lifetime.Dispose();
        }
    }
}
