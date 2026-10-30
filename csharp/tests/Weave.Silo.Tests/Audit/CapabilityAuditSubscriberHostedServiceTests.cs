using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Security.Audit;
using Weave.Security.Events;
using Weave.Shared.Events;
using Weave.Silo.Audit;

namespace Weave.Silo.Tests.Audit;

public sealed class CapabilityAuditSubscriberHostedServiceTests
{
    private static InMemoryCapabilityAuditStore CreateStore() =>
        new(Options.Create(new CapabilityAuditOptions()));

    private static CapabilityAuditSubscriberHostedService CreateSubscriber(
        IEventBus bus,
        ICapabilityAuditStore store) =>
        new(
            bus,
            store,
            TimeProvider.System,
            NullLogger<CapabilityAuditSubscriberHostedService>.Instance);

    private static CapabilityAuthorizationEvent Event(string tokenId, string grant) =>
        new()
        {
            SourceId = $"ws/{tokenId}",
            TokenId = tokenId,
            Grant = grant,
            IssuedTo = "agent",
            WorkspaceId = "ws",
            ActionContext = "Test.Op",
            Outcome = CapabilityAuthorizationOutcome.Allow,
            Reason = null
        };

    [Fact]
    public async Task StartAsync_SubscribesAndForwardsEventsToStore()
    {
        var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var store = CreateStore();
        using var hosted = CreateSubscriber(bus, store);

        await hosted.StartAsync(TestContext.Current.CancellationToken);

        await bus.PublishAsync(Event("tok-1", "tool:git"), TestContext.Current.CancellationToken);
        await bus.PublishAsync(Event("tok-1", "tool:rg"), TestContext.Current.CancellationToken);

        store.GetByToken("tok-1").Count.ShouldBe(2);

        await hosted.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StopAsync_DisposesSubscription_NoFurtherEventsRecorded()
    {
        var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var store = CreateStore();
        using var hosted = CreateSubscriber(bus, store);

        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await bus.PublishAsync(Event("tok-1", "tool:git"), TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);

        await bus.PublishAsync(Event("tok-1", "tool:rg"), TestContext.Current.CancellationToken);

        store.GetByToken("tok-1").Count.ShouldBe(1);
    }

    [Fact]
    public async Task RecordWithRetry_TransientFailure_EventuallyPersists_AndIncrementsRetriedCounter()
    {
        var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var inner = CreateStore();
        var store = new ThrowingStore(inner, throwOnAttempts: 1);
        using var hosted = CreateSubscriber(bus, store);
        var measurements = new ConcurrentQueue<CapturedMeasurement>();
        using var listener = StartCounterListener(measurements);

        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await bus.PublishAsync(Event("tok-retry", "tool:git"), TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);

        store.AttemptCount.ShouldBe(2);
        inner.GetByToken("tok-retry").Count.ShouldBe(1);
        measurements
            .Where(m => HasTag(m.Tags, "outcome", "retried"))
            .Sum(m => m.Value)
            .ShouldBe(1);
        measurements.ShouldNotContain(m => HasTag(m.Tags, "outcome", "dropped"));
    }

    [Fact]
    public async Task RecordWithRetry_AllAttemptsFail_DropsRow_AndIncrementsDroppedCounter()
    {
        var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var inner = CreateStore();
        var store = new ThrowingStore(inner, throwOnAttempts: int.MaxValue);
        using var hosted = CreateSubscriber(bus, store);
        var measurements = new ConcurrentQueue<CapturedMeasurement>();
        using var listener = StartCounterListener(measurements);

        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await bus.PublishAsync(Event("tok-drop", "tool:git"), TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);

        store.AttemptCount.ShouldBe(CapabilityAuditSubscriberHostedService.MaxAttempts);
        inner.GetByToken("tok-drop").ShouldBeEmpty();
        measurements
            .Where(m => HasTag(m.Tags, "outcome", "dropped"))
            .Sum(m => m.Value)
            .ShouldBe(1);
    }

    [Fact]
    public async Task RecordWithRetry_SuccessOnFirstAttempt_DoesNotEmitFailureMetric()
    {
        var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var store = CreateStore();
        using var hosted = CreateSubscriber(bus, store);
        var measurements = new ConcurrentQueue<CapturedMeasurement>();
        using var listener = StartCounterListener(measurements);

        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await bus.PublishAsync(Event("tok-clean", "tool:git"), TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);

        store.GetByToken("tok-clean").Count.ShouldBe(1);
        measurements.ShouldBeEmpty();
    }

    private sealed class ThrowingStore(ICapabilityAuditStore inner, int throwOnAttempts) : ICapabilityAuditStore
    {
        public int AttemptCount { get; private set; }

        public void Record(CapabilityAuthorizationEvent @event)
        {
            AttemptCount++;
            if (AttemptCount <= throwOnAttempts)
                throw new InvalidOperationException($"transient failure on attempt {AttemptCount}");
            inner.Record(@event);
        }

        public IReadOnlyList<CapabilityAuthorizationEvent> GetByToken(string tokenId) => inner.GetByToken(tokenId);
        public IReadOnlyList<CapabilityAuthorizationEvent> GetRecent(int limit) => inner.GetRecent(limit);
    }

    private sealed class CapturedMeasurement
    {
        public required long Value { get; init; }
        public required KeyValuePair<string, object?>[] Tags { get; init; }
    }

    private static MeterListener StartCounterListener(ConcurrentQueue<CapturedMeasurement> sink)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == CapabilityAuditSubscriberHostedService.MeterName
                    && instrument.Name == CapabilityAuditSubscriberHostedService.WriteFailuresCounterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            sink.Enqueue(new CapturedMeasurement { Value = value, Tags = tags.ToArray() }));
        listener.Start();
        return listener;
    }

    private static bool HasTag(KeyValuePair<string, object?>[] tags, string key, string value) =>
        tags.Any(t => t.Key == key && (string?)t.Value == value);
}
