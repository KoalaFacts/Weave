using Weave.Agents.Grains;
using Weave.Agents.Models;
using Weave.Agents.Queries;
using Weave.Shared.Ids;

namespace Weave.Agents.Tests;

/// <summary>
/// Unit tests for the query handler that lists workspace channels.
/// Mocks the grain factory so the handler's routing logic (workspace
/// id → ChannelGatewayGrain) is verified without spinning Orleans.
/// </summary>
public sealed class GetChannelsHandlerTests
{
    [Fact]
    public async Task HandleAsync_resolves_ChannelGatewayGrain_by_workspace_id()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var gateway = Substitute.For<IChannelGatewayGrain>();
        var workspaceId = WorkspaceId.From("ws-1");
        grainFactory.GetGrain<IChannelGatewayGrain>(workspaceId.ToString(), null).Returns(gateway);

        var expected = new List<ChannelConfig>
        {
            new() { ChannelId = ChannelId.From("ch-1"), Type = ChannelType.Slack, Name = "slack-1", Enabled = true },
            new() { ChannelId = ChannelId.From("ch-2"), Type = ChannelType.Email, Name = "email-1", Enabled = false }
        };
        gateway.GetChannelsAsync().Returns(expected);

        var handler = new GetChannelsHandler(grainFactory);
        var result = await handler.HandleAsync(new GetChannelsQuery(workspaceId), CancellationToken.None);

        result.ShouldBe(expected);
        grainFactory.Received(1).GetGrain<IChannelGatewayGrain>(workspaceId.ToString(), null);
    }

    [Fact]
    public async Task HandleAsync_with_empty_channel_list_returns_empty()
    {
        var grainFactory = Substitute.For<IGrainFactory>();
        var gateway = Substitute.For<IChannelGatewayGrain>();
        grainFactory.GetGrain<IChannelGatewayGrain>(Arg.Any<string>(), null).Returns(gateway);
        gateway.GetChannelsAsync().Returns(new List<ChannelConfig>());

        var handler = new GetChannelsHandler(grainFactory);
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
