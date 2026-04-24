using Weave.Agents.Actors;
using Weave.Agents.Models;
using Weave.Agents.Queries;
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
        var actorFactory = Substitute.For<IActorFactory>();
        var gateway = Substitute.For<IChannelGatewayActor>();
        var workspaceId = WorkspaceId.From("ws-1");
        actorFactory.GetActor<IChannelGatewayActor>(workspaceId.ToString(), null).Returns(gateway);

        var expected = new List<ChannelConfig>
        {
            new() { ChannelId = ChannelId.From("ch-1"), Type = ChannelType.Slack, Name = "slack-1", Enabled = true },
            new() { ChannelId = ChannelId.From("ch-2"), Type = ChannelType.Email, Name = "email-1", Enabled = false }
        };
        gateway.GetChannelsAsync().Returns(expected);

        var handler = new GetChannelsHandler(new TestVirtualActorProvider(actorFactory));
        var result = await handler.HandleAsync(new GetChannelsQuery(workspaceId), CancellationToken.None);

        result.ShouldBe(expected);
        actorFactory.Received(1).GetActor<IChannelGatewayActor>(workspaceId.ToString(), null);
    }

    [Fact]
    public async Task HandleAsync_with_empty_channel_list_returns_empty()
    {
        var actorFactory = Substitute.For<IActorFactory>();
        var gateway = Substitute.For<IChannelGatewayActor>();
        actorFactory.GetActor<IChannelGatewayActor>(Arg.Any<string>(), null).Returns(gateway);
        gateway.GetChannelsAsync().Returns(new List<ChannelConfig>());

        var handler = new GetChannelsHandler(new TestVirtualActorProvider(actorFactory));
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
