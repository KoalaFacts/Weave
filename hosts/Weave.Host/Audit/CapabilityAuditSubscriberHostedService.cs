using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Weave.Security.Audit;
using Weave.Security.Events;
using Weave.Shared.Events;

namespace Weave.Silo.Audit;

/// <summary>
/// Subscribes to <see cref="CapabilityAuthorizationEvent"/> on the silo's
/// <see cref="IEventBus"/> and feeds rows into the <see cref="ICapabilityAuditStore"/>.
/// Powers roadmap #3 — capability replay/debugger.
/// </summary>
/// <remarks>
/// Startup ordering note: any authorize call that runs before this hosted
/// service's <see cref="StartAsync"/> lands its subscription will not be
/// recorded. In practice the silo activates plugin/agent grains lazily on
/// first request, so this gap closes before any external traffic — but a
/// future hosted service that authorizes during its own startup may drop
/// rows. If that becomes a concern, register this service first or move the
/// subscription into the service registrar.
/// </remarks>
public sealed partial class CapabilityAuditSubscriberHostedService(
    IEventBus eventBus,
    ICapabilityAuditStore store,
    TimeProvider timeProvider,
    ILogger<CapabilityAuditSubscriberHostedService> logger) : IHostedService, IDisposable
{
    /// <summary>Meter name. Registered in <c>Weave.ServiceDefaults.Extensions.ConfigureOpenTelemetry</c>.</summary>
    public const string MeterName = "Weave.Silo.Audit";

    /// <summary>
    /// Counter name. Tagged with <c>outcome</c> — <c>"retried"</c> for a transient
    /// failure that succeeded on a later attempt, <c>"dropped"</c> for a row that
    /// failed every attempt and was discarded. Pair with the deny-rate metric to
    /// alert when audit writes drop without denies dropping.
    /// </summary>
    public const string WriteFailuresCounterName = "weave.silo.audit.write_failures";

    internal const int MaxAttempts = 3;
    internal static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(500);

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> WriteFailures = Meter.CreateCounter<long>(
        WriteFailuresCounterName,
        unit: "{failure}",
        description: "Capability audit-store write failures, tagged by outcome (retried|dropped).");

    private readonly CancellationTokenSource _stopping = new();
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = eventBus.Subscribe<CapabilityAuthorizationEvent>((evt, ct) =>
            RecordWithRetryAsync(evt, ct));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _stopping.Dispose();

    private async Task RecordWithRetryAsync(CapabilityAuthorizationEvent evt, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                store.Record(evt);
                if (attempt > 1)
                {
                    WriteFailures.Add(1, new KeyValuePair<string, object?>("outcome", "retried"));
                    LogPersistedAfterRetry(evt.TokenId, attempt);
                }
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                LogAttemptFailed(ex, evt.TokenId, attempt, MaxAttempts);
            }

            if (attempt == MaxAttempts)
                break;

            var delay = ComputeBackoff(attempt);
            try
            {
                await Task.Delay(delay, timeProvider, linked.Token);
            }
            catch (OperationCanceledException)
            {
                // Shutting down — give up without further retries.
                break;
            }
        }

        WriteFailures.Add(1, new KeyValuePair<string, object?>("outcome", "dropped"));
        LogDropped(lastError, evt.TokenId, evt.Grant);
    }

    private static TimeSpan ComputeBackoff(int attempt)
    {
        // Exponential backoff with full jitter: 0..min(MaxDelay, BaseDelay * 2^(attempt-1)).
        var ceiling = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, BaseDelay.Ticks * (1L << (attempt - 1))));
        var jittered = (long)(Random.Shared.NextDouble() * ceiling.Ticks);
        return TimeSpan.FromTicks(jittered);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Capability audit write failed for token '{TokenId}' (attempt {Attempt}/{MaxAttempts}); will retry if attempts remain")]
    private partial void LogAttemptFailed(Exception ex, string tokenId, int attempt, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Capability audit write for token '{TokenId}' persisted on attempt {Attempt} after transient failure")]
    private partial void LogPersistedAfterRetry(string tokenId, int attempt);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Capability audit write dropped for token '{TokenId}' grant '{Grant}' after exhausting retries")]
    private partial void LogDropped(Exception? ex, string tokenId, string grant);
}
