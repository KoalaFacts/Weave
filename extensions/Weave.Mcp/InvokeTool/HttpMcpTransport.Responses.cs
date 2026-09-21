using System.Buffers;
using System.Text;

namespace Weave.Tools.Connectors;

// Parsing belongs to this transport, not a second public protocol or SDK.
internal sealed partial class HttpMcpTransport
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private async Task ConsumeJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var limit = Math.Min(_maxFrameBytes, _maxResponseBytes);
        if (response.Content.Headers.ContentLength is long declared && declared > limit)
            throw Failure($"JSON response Content-Length {declared} exceeds limit {limit}");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var bounded = new McpResponseReadStream(stream, limit);
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        using var sink = new MemoryStream();
        try
        {
            int read;
            while ((read = await bounded.ReadAsync(buffer.AsMemory(), ct)) > 0)
                sink.Write(buffer, 0, read);
        }
        catch (IOException) when (bounded.LimitExceeded)
        {
            throw Failure(_maxResponseBytes <= _maxFrameBytes
                ? $"response body exceeded limit {_maxResponseBytes}"
                : $"JSON response body exceeded frame limit {_maxFrameBytes}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var body = StrictUtf8.GetString(sink.GetBuffer(), 0, (int)sink.Length);
        await _incoming.Writer.WriteAsync(body, ct);
    }

    private async Task ConsumeEventStreamAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is long declared && declared > _maxResponseBytes)
            throw Failure($"SSE Content-Length {declared} exceeds total-bytes limit {_maxResponseBytes}");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var bounded = new McpResponseReadStream(stream, _maxResponseBytes);
        using var reader = new StreamReader(bounded, StrictUtf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var data = new StringBuilder();
        long frameBytes = 0;
        var hasData = false;
        var firstLine = true;

        while (true)
        {
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idleCts.CancelAfter(_idleTimeout);
            string? line;
            try
            {
                line = await reader.ReadLineAsync(idleCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw Failure($"SSE idle timeout ({_idleTimeout.TotalSeconds:0}s)");
            }
            catch (IOException) when (bounded.LimitExceeded)
            {
                throw Failure($"SSE stream exceeded total-bytes limit {_maxResponseBytes}");
            }

            if (line is null)
                break;
            // SSE permits one initial UTF-8 BOM, not automatic UTF-16/32 decoding.
            if (firstLine && line.StartsWith('\uFEFF'))
                line = line[1..];
            firstLine = false;

            if (line.Length == 0)
            {
                if (hasData)
                {
                    await _incoming.Writer.WriteAsync(data.ToString(), ct);
                    data.Clear();
                    frameBytes = 0;
                    hasData = false;
                }
                continue;
            }

            if (line == "data" || line.StartsWith("data:", StringComparison.Ordinal))
            {
                var payload = line == "data" ? ReadOnlySpan<char>.Empty
                    : line.AsSpan(line.Length > 5 && line[5] == ' ' ? 6 : 5);
                frameBytes += StrictUtf8.GetByteCount(payload) + (hasData ? 1L : 0L);
                if (frameBytes > _maxFrameBytes)
                    throw Failure($"SSE data frame exceeded limit {_maxFrameBytes}");
                if (hasData)
                    data.Append('\n');
                data.Append(payload);
                hasData = true;
            }
        }

        // Do not manufacture a complete RPC result from an unfinished event.
        if (hasData)
            throw Failure("SSE stream ended with an incomplete data frame");
    }
}
