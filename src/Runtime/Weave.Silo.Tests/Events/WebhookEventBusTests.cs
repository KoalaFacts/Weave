using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Shared.Events;
using Weave.Silo.Events;
using static Weave.Silo.Tests.Channels.ChannelAdapterTestHelpers;

namespace Weave.Silo.Tests.Events;

public sealed class WebhookEventBusTests
{
    private sealed record TestEvent(string Payload) : IDomainEvent
    {
        public string EventId { get; init; } = Guid.NewGuid().ToString("N");
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
        public string SourceId { get; init; } = "test-source";
    }

    private static WebhookEventBus Build(CapturingHandler handler, Uri? url = null) =>
        new(new HttpClient(handler), url ?? new Uri("https://hook.example.com/events"), NullLogger<WebhookEventBus>.Instance);

    [Fact]
    public async Task PublishAsync_PostsSerializedEventWithTopicHeader()
    {
        var handler = new CapturingHandler();
        var bus = Build(handler);
        var evt = new TestEvent("hello");

        await bus.PublishAsync(evt, TestContext.Current.CancellationToken);

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString().ShouldBe("https://hook.example.com/events");
        handler.LastRequest.Content!.Headers.GetValues("X-Weave-Topic").ShouldContain("TestEvent");
        AssertJsonPayload(handler.LastRequestBody, json =>
            json.GetProperty("Payload").GetString().ShouldBe("hello"));
    }

    [Fact]
    public async Task PublishAsync_WebhookFails_StillDispatchesLocalHandlers()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError);
        var bus = Build(handler);
        var received = new List<string>();
        using var _ = bus.Subscribe<TestEvent>((e, _) => { received.Add(e.Payload); return Task.CompletedTask; });

        await bus.PublishAsync(new TestEvent("resilient"), TestContext.Current.CancellationToken);

        received.ShouldBe(["resilient"], "webhook failure must not suppress local handlers — that's the resilience contract");
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

        survivingHandlerRan.ShouldBeTrue("a failing handler must not prevent other handlers from running");
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

        count.ShouldBe(1, "handler must not fire after its subscription is disposed");
    }

    [Fact]
    public async Task PublishAsync_WithNoSubscribers_StillPostsToWebhook()
    {
        var handler = new CapturingHandler();
        var bus = Build(handler);

        await bus.PublishAsync(new TestEvent("alone"), TestContext.Current.CancellationToken);

        handler.LastRequest.ShouldNotBeNull();
    }
}
