using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Weave.Agents.Channels;
using Weave.Agents.Lifecycle;
using Weave.Agents.Memory;
using Weave.Agents.Skills;
using Weave.Agents.ToolRegistry;
using Weave.Agents.Users;
using Weave.Agents.Verification;
using Weave.Tools.Connectors;
using Weave.Tools.Tool;
using Weave.Shared.VirtualActors;
using Weave.Invocations;
using Weave.Workspaces.Manifest;

namespace Weave.Silo.Tests.Plugins;

public sealed class DaprWorkspacePluginFlowTests : IClassFixture<SiloFactory>
{
    private readonly SiloFactory _factory;

    public DaprWorkspacePluginFlowTests(SiloFactory factory) => _factory = factory;

    [Fact]
    public async Task Workspace_DaprToolDependency_ActivatesOnlyWithExplicitInvocationGrantAndStops()
    {
        var sidecarBuilder = WebApplication.CreateBuilder();
        sidecarBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var sidecar = sidecarBuilder.Build();
        sidecar.MapPost("/v1.0/invoke/echo-service/method/ping", () => Results.Json(new { pong = true }));
        await sidecar.StartAsync(TestContext.Current.CancellationToken);
        var address = sidecar.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        var port = new Uri(address).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        using var client = _factory.CreateClient();
        var manifest = new WorkspaceManifest
        {
            Version = "1.0",
            Name = $"dapr-tool-{Guid.NewGuid():N}",
            Plugins = new Dictionary<string, PluginDefinition>
            {
                ["sidecar"] = new()
                {
                    Type = "dapr_tools",
                    Config = new Dictionary<string, string> { ["port"] = port }
                }
            },
            Tools = new Dictionary<string, ToolDefinition>
            {
                ["echo"] = new()
                {
                    Type = "dapr",
                    RequiresPlugin = "sidecar",
                    Dapr = new DaprToolConfig { AppId = "echo-service" }
                }
            },
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["observer"] = new() { Model = "test", Tools = ["echo"] },
                ["caller"] = new()
                {
                    Model = "test",
                    Tools = ["echo"],
                    Capabilities = ["tool:echo:invoke:ping"]
                }
            }
        };

        using var start = await client.PostAsJsonAsync("/api/workspaces",
            new { Manifest = manifest }, TestContext.Current.CancellationToken);
        var body = await start.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        start.StatusCode.ShouldBe(HttpStatusCode.Created, body);
        using var document = JsonDocument.Parse(body);
        var workspaceId = document.RootElement.GetProperty("workspaceId").GetString()!;

        var actors = _factory.Services.GetRequiredService<IVirtualActorProvider>();
        var registry = actors.GetActor<IToolRegistryActor>(VirtualActorId.From(workspaceId));
        (await registry.GetConnectionAsync("echo"))!.Status.ShouldBe(ToolConnectionStatus.Connected);
        (await registry.ResolveAsync("observer", "echo")).ShouldBeNull();
        var authorized = await registry.ResolveAsync("caller", "echo");
        authorized.ShouldNotBeNull();
        authorized.Token.Grants.ShouldContain("tool:echo:invoke:ping");
        var actor = actors.GetActor<IToolActor>(VirtualActorId.From($"{workspaceId}/echo"));
        (await actor.GetHandleAsync())!.ConnectionId.ShouldBe("dapr:echo-service");
        var result = await actor.InvokeAsync(new ToolInvocation
        {
            ToolName = "echo",
            Method = "ping",
            InvocationId = InvocationId.From(Guid.NewGuid().ToString("N"))
        }, authorized.Token);
        result.Success.ShouldBeTrue(result.Error);
        result.Output.ShouldContain("pong");
        result.OutcomeRecorded.ShouldBeTrue();

        using var composition = await client.GetAsync("/api/plugins/composition",
            TestContext.Current.CancellationToken);
        var active = await composition.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        active.ShouldContain($"{workspaceId}/sidecar");

        using var disable = await client.DeleteAsync($"/api/plugins/{workspaceId}/sidecar",
            TestContext.Current.CancellationToken);
        disable.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await Should.ThrowAsync<InvalidOperationException>(() => actor.InvokeAsync(new ToolInvocation
        {
            ToolName = "echo",
            Method = "ping",
            InvocationId = InvocationId.From(Guid.NewGuid().ToString("N"))
        }, authorized.Token));

        using var reactivate = await client.PostAsJsonAsync("/api/plugins", new
        {
            Name = $"{workspaceId}/sidecar",
            Type = "dapr_tools",
            Config = new Dictionary<string, string> { ["port"] = port }
        }, TestContext.Current.CancellationToken);
        reactivate.StatusCode.ShouldBe(HttpStatusCode.Created);
        await Should.ThrowAsync<InvalidOperationException>(() => actor.InvokeAsync(new ToolInvocation
        {
            ToolName = "echo",
            Method = "ping",
            InvocationId = InvocationId.From(Guid.NewGuid().ToString("N"))
        }, authorized.Token));

        using var stop = await client.DeleteAsync($"/api/workspaces/{workspaceId}",
            TestContext.Current.CancellationToken);
        stop.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await actor.GetHandleAsync()).ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(() => actor.InvokeAsync(new ToolInvocation
        {
            ToolName = "echo",
            Method = "ping",
            InvocationId = InvocationId.From(Guid.NewGuid().ToString("N"))
        }, authorized.Token));
        using var after = await client.GetAsync("/api/plugins/composition",
            TestContext.Current.CancellationToken);
        (await after.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldNotContain($"{workspaceId}/sidecar");
    }
}
