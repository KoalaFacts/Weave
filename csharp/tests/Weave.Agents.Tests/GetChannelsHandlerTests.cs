using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

/// <summary>
/// Unit tests for the query handler that lists workspace channels.
/// Mocks the actor factory so the handler's routing logic (workspace
/// id → ChannelGatewayActor) is verified without spinning Orleans.
/// </summary>
public sealed class GetChannelsHandlerTests
{
    [Fact]
    public async Task HandleAsync_resolves_ChannelGatewayActor_by_workspace_id()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var gateway = Substitute.For<IChannelGatewayActor>();
        var workspaceId = WorkspaceId.From("ws-1");
        actors.GetActor<IChannelGatewayActor>(Arg.Any<VirtualActorId>()).Returns(gateway);

        var expected = new List<ChannelConfig>
        {
            new() { ChannelId = ChannelId.From("ch-1"), Type = ChannelType.Slack, Name = "slack-1", Enabled = true },
            new() { ChannelId = ChannelId.From("ch-2"), Type = ChannelType.Email, Name = "email-1", Enabled = false }
        };
        gateway.GetChannelsAsync().Returns(expected);

        var handler = new GetChannelsHandler(actors);
        var result = await handler.HandleAsync(new GetChannelsQuery(workspaceId), CancellationToken.None);

        result.ShouldBe(expected);
        actors.Received(1).GetActor<IChannelGatewayActor>(Arg.Any<VirtualActorId>());
    }

    [Fact]
    public async Task HandleAsync_with_empty_channel_list_returns_empty()
    {
        var actors = Substitute.For<IVirtualActorProvider>();
        var gateway = Substitute.For<IChannelGatewayActor>();
        actors.GetActor<IChannelGatewayActor>(Arg.Any<VirtualActorId>()).Returns(gateway);
        gateway.GetChannelsAsync().Returns(new List<ChannelConfig>());

        var handler = new GetChannelsHandler(actors);
        var result = await handler.HandleAsync(
            new GetChannelsQuery(WorkspaceId.From("empty-ws")), CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public void GetChannelsQuery_carries_the_workspace_id()
    {
        var ws = WorkspaceId.From("query-ws");
        var query = new GetChannelsQuery(ws);

        query.WorkspaceId.ShouldBe(ws);
    }
}
