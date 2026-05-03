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
        var hosted = new CapabilityAuditSubscriberHostedService(bus, store);

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
        var hosted = new CapabilityAuditSubscriberHostedService(bus, store);

        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await bus.PublishAsync(Event("tok-1", "tool:git"), TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);

        await bus.PublishAsync(Event("tok-1", "tool:rg"), TestContext.Current.CancellationToken);

        store.GetByToken("tok-1").Count.ShouldBe(1);
    }
}
