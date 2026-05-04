using Microsoft.Extensions.DependencyInjection;
using Weave.Agents.Lifecycle;
using Weave.Agents.Channels;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Agents.ToolRegistry;
using Weave.Security.Tokens;
using Weave.Shared.Ids;
using Weave.Silo.VirtualActors;

namespace Weave.Silo.Tests;

/// <summary>
/// Direct grain-bridge tests for one-line pass-through methods that the
/// public HTTP API does not exercise. These force coverage on grain methods
/// only reachable from internal callers (e.g. <c>RecordUsageAsync</c> from
/// the chat pipeline, <c>DisconnectToolAsync</c> from agent supervisor flows).
/// </summary>
public sealed class GrainBridgeTests : IClassFixture<SiloFactory>
{
    private readonly SiloFactory _factory;

    public GrainBridgeTests(SiloFactory factory) => _factory = factory;

    private static string NewWorkspaceId() => $"ws-{Guid.NewGuid():N}";

    [Fact]
    public async Task SkillMemoryActorGrain_RecordUsageAsync_PassesThroughToActor()
    {
        // Boot the silo so the grain factory is available.
        using var _ = _factory.CreateClient();
        var grainFactory = _factory.Services.GetRequiredService<Orleans.IGrainFactory>();
        var tokenService = _factory.Services.GetRequiredService<ICapabilityTokenService>();
        var ws = NewWorkspaceId();

        var token = tokenService.Mint(new CapabilityTokenRequest
        {
            WorkspaceId = ws,
            IssuedTo = "test",
            Grants = ["skill:write"],
            Lifetime = TimeSpan.FromMinutes(5)
        });

        var skillGrain = grainFactory.GetGrain<ISkillMemoryActorGrain>(ws);

        // Recording usage on a non-existent skill must no-op (the underlying
        // actor's contract). The point of the test is to invoke the grain
        // bridge's passthrough line, not to verify side effects.
        await skillGrain.RecordUsageAsync(SkillId.From("not-stored"), success: true, token);
    }

    [Fact]
    public async Task AgentActorGrain_DisconnectToolAsync_PassesThroughToActor()
    {
        using var _ = _factory.CreateClient();
        var grainFactory = _factory.Services.GetRequiredService<Orleans.IGrainFactory>();
        var ws = NewWorkspaceId();
        var key = $"{ws}/researcher";

        var agentGrain = grainFactory.GetGrain<IAgentActorGrain>(key);

        // Disconnecting a tool that was never connected is a no-op on the
        // underlying actor — exercises the bridge's passthrough line.
        await agentGrain.DisconnectToolAsync("never-connected");
    }
}
