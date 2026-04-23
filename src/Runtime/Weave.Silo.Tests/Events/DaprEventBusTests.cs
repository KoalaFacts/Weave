using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Shared.Events;
using Weave.Silo.Events;
using static Weave.Silo.Tests.Channels.ChannelAdapterTestHelpers;

namespace Weave.Silo.Tests.Events;

public sealed class DaprEventBusTests
{
    private sealed record TestEvent(string Payload) : IDomainEvent
    {
        public string EventId { get; init; } = Guid.NewGuid().ToString("N");
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
        public string SourceId { get; init; } = "test-source";
    }

    private static DaprEventBus Build(CapturingHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:3500") }, NullLogger<DaprEventBus>.Instance);

    [Fact]
    public async Task PublishAsync_PostsToDaprPublishEndpointWithEventTypeAsTopic()
    {
        var handler = new CapturingHandler();
        var bus = Build(handler);
        var evt = new TestEvent("hello dapr");

        await bus.PublishAsync(evt, TestContext.Current.CancellationToken);

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/v1.0/publish/pubsub/TestEvent");
        AssertJsonPayload(handler.LastRequestBody, json =>
            json.GetProperty("Payload").GetString().ShouldBe("hello dapr"));
    }

    [Fact]
    public async Task PublishAsync_SidecarFails_StillDispatchesLocalHandlers()
    {
        var handler = new CapturingHandler(HttpStatusCode.ServiceUnavailable);
        var bus = Build(handler);
        var received = new List<string>();
        using var _ = bus.Subscribe<TestEvent>((e, _) => { received.Add(e.Payload); return Task.CompletedTask; });

        await bus.PublishAsync(new TestEvent("fallback"), TestContext.Current.CancellationToken);

        received.ShouldBe(["fallback"], "Dapr sidecar failure must not suppress local handlers");
    }

    [Fact]
    public async Task PublishAsync_InvokesAllSubscribedHandlers()
    {
        var bus = Build(new CapturingHandler());
        var a = 0;
        var b = 0;
        using var _1 = bus.Subscribe<TestEvent>((_, _) => { a++; return Task.CompletedTask; });
        using var _2 = bus.Subscribe<TestEvent>((_, _) => { b++; return Task.CompletedTask; });

        await bus.PublishAsync(new TestEvent("x"), TestContext.Current.CancellationToken);

        a.ShouldBe(1);
        b.ShouldBe(1);
    }

    [Fact]
    public async Task PublishAsync_OneHandlerThrows_OtherHandlersStillRun()
    {
        var bus = Build(new CapturingHandler());
        var survivingHandlerRan = false;
        using var _1 = bus.Subscribe<TestEvent>((_, _) => throw new InvalidOperationException("boom"));
        using var _2 = bus.Subscribe<TestEvent>((_, _) => { survivingHandlerRan = true; return Task.CompletedTask; });

        await bus.PublishAsync(new TestEvent("x"), TestContext.Current.CancellationToken);

        survivingHandlerRan.ShouldBeTrue();
    }

    [Fact]
    public async Task Subscribe_Dispose_RemovesHandler()
    {
        var bus = Build(new CapturingHandler());
        var count = 0;
        var sub = bus.Subscribe<TestEvent>((_, _) => { count++; return Task.CompletedTask; });

        await bus.PublishAsync(new TestEvent("first"), TestContext.Current.CancellationToken);
        sub.Dispose();
        await bus.PublishAsync(new TestEvent("second"), TestContext.Current.CancellationToken);

        count.ShouldBe(1);
    }
}
