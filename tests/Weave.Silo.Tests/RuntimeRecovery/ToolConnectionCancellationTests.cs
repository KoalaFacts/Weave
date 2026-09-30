using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weave.Security.Tokens;
using Weave.Shared.Lifecycle;
using Weave.Shared.VirtualActors;
using Weave.Tools.Connectors;
using Weave.Tools.Tool;

namespace Weave.Silo.Tests.RuntimeRecovery;

public sealed class ToolConnectionCancellationTests
{
    [Fact]
    public async Task ConnectAsync_CancelledAcrossGrainBoundary_StopsConnectionHook()
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycle = Substitute.For<ILifecycleManager>();
        lifecycle.RunHooksAsync(LifecyclePhase.ToolConnecting, Arg.Any<LifecycleContext>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var ct = call.Arg<CancellationToken>();
                entered.TrySetResult(ct);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            });
        await using var parent = new SiloFactory();
        await using var host = parent.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ILifecycleManager>();
            services.AddSingleton(lifecycle);
        }));
        using var client = host.CreateClient();
        var tool = host.Services.GetRequiredService<IVirtualActorProvider>()
            .GetActor<IToolActor>(VirtualActorId.From("recovery/tool"));
        var token = host.Services.GetRequiredService<ICapabilityTokenService>().Mint(new()
        {
            WorkspaceId = "recovery",
            IssuedTo = "operator",
            Grants = ["tool:tool:connect"],
            Lifetime = TimeSpan.FromMinutes(1)
        });
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var connecting = tool.ConnectAsync(new() { Name = "tool", Type = ToolType.Cli }, token, caller.Token);
        try
        {
            var received = await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            caller.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => connecting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            received.IsCancellationRequested.ShouldBeTrue();
            (await tool.GetHandleAsync()).ShouldBeNull();
        }
        finally
        {
            caller.Cancel();
            await ((Task)connecting).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }
}
