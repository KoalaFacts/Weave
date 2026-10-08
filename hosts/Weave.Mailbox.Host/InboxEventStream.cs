using System.Text.Json;
using Weave.Mailboxes;

namespace Weave.Mailbox.Host;

internal sealed class InboxEventStream(IExpiringMailboxStore store, MailboxHostOptions options,
    MailboxControlAuthentication authentication, TimeProvider timeProvider)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _streams = new(StringComparer.Ordinal);
    private int _total;

    internal async Task RunAsync(HttpContext context)
    {
        var scope = context.Features.Get<MailboxControlScope>()!;
        if (!Acquire(scope.Authority.MailboxId.Value))
        { await MailboxHttp.ErrorAsync(context, 429, "capacity"); return; }
        using var lifetime = new CancellationTokenSource(options.StreamLifetime, timeProvider);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.Token);
        var ct = cancelled.Token;
        context.Response.ContentType = "text/event-stream";
        try
        {
            await FlushAsync(context, ": pending\n\n"u8.ToArray(), cancelled);
            while (!ct.IsCancellationRequested && authentication.IsCurrent(scope))
            {
                // Reconnect watermarks never replace recipient ACK. Each cycle starts with all live pending data.
                string? cursor = null;
                do
                {
                    if (!authentication.IsCurrent(scope))
                        return;
                    var page = store.ReadPending(scope.Authority, cursor, 1, ct);
                    if (page.Items.IsEmpty)
                        break;
                    var message = page.Items[0];
                    var json = JsonSerializer.SerializeToUtf8Bytes(MailboxWireMapping.Message(message), MailboxJsonContext.Default.ReceivedMessageWire);
                    var prefix = System.Text.Encoding.UTF8.GetBytes($"id: {message.Envelope.MessageId:D}\nevent: message\ndata: ");
                    var frame = new byte[prefix.Length + json.Length + 2];
                    prefix.CopyTo(frame, 0);
                    json.CopyTo(frame, prefix.Length);
                    frame[^2] = (byte)'\n';
                    frame[^1] = (byte)'\n';
                    if (!authentication.IsCurrent(scope))
                        return;
                    await FlushAsync(context, frame, cancelled);
                    cursor = page.NextCursor;
                } while (cursor is not null);
                await Task.Delay(options.PollInterval, timeProvider, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { Release(scope.Authority.MailboxId.Value); }
    }
    private async Task FlushAsync(HttpContext context, byte[] bytes, CancellationTokenSource cancelled)
    {
        var ct = cancelled.Token;
        using var timeout = new CancellationTokenSource(options.StreamWriteTimeout, timeProvider);
        using var write = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await context.Response.Body.WriteAsync(bytes, write.Token);
            await context.Response.Body.FlushAsync(write.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            cancelled.Cancel();
            context.Abort();
            throw;
        }
    }
    private bool Acquire(string owner)
    {
        lock (_gate)
        {
            var count = _streams.GetValueOrDefault(owner);
            if (_total >= options.MaximumStreams || count >= options.MaximumStreamsPerMailbox)
                return false;
            _streams[owner] = count + 1;
            _total++;
            return true;
        }
    }
    private void Release(string owner)
    {
        lock (_gate)
        {
            if (_streams[owner] == 1)
                _streams.Remove(owner);
            else
                _streams[owner]--;
            _total--;
        }
    }
}
