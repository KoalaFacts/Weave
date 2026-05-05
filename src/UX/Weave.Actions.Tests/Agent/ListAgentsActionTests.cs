using Weave.Actions;
using Weave.Actions.Agent;
using Weave.Actions.Context;

namespace Weave.Actions.Tests.Agent;

public sealed class ListAgentsActionTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsAgentsFromSilo()
    {
        var siloApi = Substitute.For<ISiloApi>();
        var agents = new[]
        {
            new AgentSummary { AgentName = "alpha", Status = "running", Model = "gpt", ActiveTasksCount = 1, ConnectedToolsCount = 2 },
            new AgentSummary { AgentName = "beta", Status = "idle", Model = null, ActiveTasksCount = 0, ConnectedToolsCount = 0 }
        };
        siloApi.ListAgentsAsync("ws-1", Arg.Any<CancellationToken>()).Returns(agents);

        var action = new ListAgentsAction(siloApi);
        var result = await action.ExecuteAsync(new ListAgentsInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Agents.Count.ShouldBe(2);
        result.Value.Agents[0].AgentName.ShouldBe("alpha");
        result.Value.Agents[1].AgentName.ShouldBe("beta");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyAgents_ReturnsSuccessWithEmptyList()
    {
        var siloApi = Substitute.For<ISiloApi>();
        siloApi.ListAgentsAsync("ws-1", Arg.Any<CancellationToken>()).Returns([]);

        var action = new ListAgentsAction(siloApi);
        var result = await action.ExecuteAsync(new ListAgentsInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Agents.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_HttpRequestException_ReturnsSiloUnreachable()
    {
        var siloApi = Substitute.For<ISiloApi>();
        siloApi.ListAgentsAsync("ws-1", Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<AgentSummary>>>(_ => throw new HttpRequestException("connection refused"));

        var action = new ListAgentsAction(siloApi);
        var result = await action.ExecuteAsync(new ListAgentsInput("ws-1"), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.SiloUnreachable);
        result.Failure.Message.ShouldContain("connection refused");
    }

    [Fact]
    public async Task ExecuteAsync_PassesCancellationTokenAndWorkspaceId()
    {
        using var cts = new CancellationTokenSource();
        var siloApi = Substitute.For<ISiloApi>();
        siloApi.ListAgentsAsync("ws-42", cts.Token).Returns([]);

        var action = new ListAgentsAction(siloApi);
        await action.ExecuteAsync(new ListAgentsInput("ws-42"), cts.Token);

        await siloApi.Received(1).ListAgentsAsync("ws-42", cts.Token);
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        var action = new ListAgentsAction(Substitute.For<ISiloApi>());

        await Should.ThrowAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_BlankWorkspaceId_Throws(string workspaceId)
    {
        var action = new ListAgentsAction(Substitute.For<ISiloApi>());

        await Should.ThrowAsync<ArgumentException>(
            () => action.ExecuteAsync(new ListAgentsInput(workspaceId), CancellationToken.None));
    }
}
