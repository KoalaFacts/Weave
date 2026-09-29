using Microsoft.AspNetCore.Http;
using Weave.Security.Tokens;
using Weave.Shared.VirtualActors;
using Weave.Silo.Api;
using Weave.Silo.Plugins;
using Weave.Tools.InstallDaprTool;
using Weave.Workspaces.Lifecycle;

namespace Weave.Silo.Tests.Plugins;

public sealed class DaprInstallationAmbiguityTests
{
    [Fact]
    public async Task DisconnectPluginAsync_AmbiguousStoredDaprId_ReturnsConflictWithoutDispatch()
    {
        var workspace = Substitute.For<IWorkspaceActor>();
        workspace.GetStateAsync().Returns(new WorkspaceState
        {
            DaprToolInstallations =
            [
                new DaprToolInstallation { Id = "ws-1/sidecar", PluginName = "sidecar" },
                new DaprToolInstallation { Id = "ws-1/SIDECAR", PluginName = "SIDECAR" }
            ]
        });
        var actors = Substitute.For<IVirtualActorProvider>();
        actors.GetActor<IWorkspaceActor>(VirtualActorId.From("ws-1")).Returns(workspace);
        var registry = Substitute.For<IPluginRegistry>();
        var authority = new UnexpectedAuthority();

        var result = await PluginEndpoints.DisconnectPluginAsync(
            "ws-1/sidecar", registry, Substitute.For<ICapabilityTokenService>(),
            authority, new DefaultHttpContext(), actors,
            Substitute.For<IMcpInstallationDispatchGate>(),
            Substitute.For<IInstallationDiagnostics>(), null!, TestContext.Current.CancellationToken);

        result.ShouldBeAssignableTo<IStatusCodeHttpResult>().StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        await workspace.DidNotReceiveWithAnyArgs().SetDaprToolInstallationEnabledAsync(default!, default);
    }

    [Fact]
    public async Task DisconnectPluginAsync_WorkspaceReadFails_PreservesFailure()
    {
        var workspace = Substitute.For<IWorkspaceActor>();
        workspace.GetStateAsync().Returns(Task.FromException<WorkspaceState>(
            new InvalidOperationException("state storage unavailable")));
        var actors = Substitute.For<IVirtualActorProvider>();
        actors.GetActor<IWorkspaceActor>(VirtualActorId.From("ws-1")).Returns(workspace);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => PluginEndpoints.DisconnectPluginAsync(
            "ws-1/sidecar", Substitute.For<IPluginRegistry>(), Substitute.For<ICapabilityTokenService>(),
            new UnexpectedAuthority(), new DefaultHttpContext(), actors,
            Substitute.For<IMcpInstallationDispatchGate>(),
            Substitute.For<IInstallationDiagnostics>(), null!, TestContext.Current.CancellationToken));

        error.Message.ShouldBe("state storage unavailable");
    }

    private sealed class UnexpectedAuthority : IPluginInstallationAuthority
    {
        public Task<IResult?> DenialAsync(HttpContext context, string workspaceId, params string[] grants) =>
            throw new InvalidOperationException("Ambiguous installation must stop before authorization.");
    }
}
