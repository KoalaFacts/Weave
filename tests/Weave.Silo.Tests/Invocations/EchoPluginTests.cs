using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Invocations;
using Weave.Security.Actors;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Shared.VirtualActors;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Tools.Tool;
using Weave.Workspaces.Manifest;

namespace Weave.Silo.Tests.Invocations;

// Included only in the independent examples job via EchoPluginExamples=true.
public sealed class EchoPluginTests
{
    [Fact]
    public async Task Invoke_RealEchoPlugin_DeniesMissingGrantAndRecordsOneAllowedDispatch()
    {
        var path = Environment.GetEnvironmentVariable("WEAVE_ECHO_SPEC")
            ?? throw new InvalidOperationException("Set WEAVE_ECHO_SPEC to the sample tool.json.");
        var root = RepositoryRoot();
        string Resolve(string value) => string.Join(Path.PathSeparator,
            value.Split(Path.PathSeparator).Select(part => part.StartsWith("examples/plugins/echo/", StringComparison.Ordinal)
                ? Path.GetFullPath(part, root) : part));
        using var config = JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(path, root)));
        var mcp = config.RootElement.GetProperty("mcp");
        var spec = new ToolSpec
        {
            Name = config.RootElement.GetProperty("name").GetString()!,
            Type = ToolType.Mcp,
            Mcp = new McpConfig
            {
                Server = Resolve(mcp.GetProperty("server").GetString()!),
                Args = [.. mcp.GetProperty("args").EnumerateArray().Select(a => Resolve(a.GetString()!))]
            }
        };
        spec.Name.ShouldBe("echo-sample");
        using var fx = new ApprovalScenario();
        var actors = Substitute.For<IVirtualActorProvider>();
        actors.GetActor<ISecretProxyActor>(Arg.Any<VirtualActorId>()).Returns(fx.Secrets);
        var events = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var connector = new ObservedConnector(new McpToolConnector(NullLogger<McpToolConnector>.Instance));
        var actor = new ToolActor(actors,
            new ToolDiscoveryService([connector], NullLogger<ToolDiscoveryService>.Instance),
            new LeakScanner(NullLogger<LeakScanner>.Instance),
            new CapabilityAuthorizer(fx.Tokens, events, NullLogger<CapabilityAuthorizer>.Instance),
            new LifecycleManager(NullLogger<LifecycleManager>.Instance), events,
            NullLogger<ToolActor>.Instance, fx.Journal, fx.Clock);
        await actor.OnActivatedAsync("workspace/echo-sample", TestContext.Current.CancellationToken);
        await actor.ConnectAsync(spec, fx.Token("setup", "tool:echo-sample:connect"));
        try
        {
            (await actor.GetSchemaAsync()).Description.ShouldContain("Return the supplied text unchanged.");
            var id = InvocationId.From(Guid.NewGuid().ToString("N"));
            const string text = "你好，Weave 🙂\r\nA second line.";
            var invocation = new ToolInvocation
            {
                InvocationId = id,
                ToolName = "echo-sample",
                Method = "echo",
                Parameters = new() { ["text"] = text }
            };
            await Should.ThrowAsync<UnauthorizedAccessException>(() => actor.InvokeAsync(invocation,
                fx.Token("agent", "tool:echo-sample:connect")));
            connector.Calls.ShouldBe(0);
            fx.Journal.Find("workspace", id, TestContext.Current.CancellationToken).ShouldBeNull();

            var token = fx.Token("agent", "tool:echo-sample:invoke:echo", "invocation:read");
            var result = await actor.InvokeAsync(invocation, token);
            result.Success.ShouldBeTrue(result.Error);
            result.Output.ShouldBe(text);
            result.Outcome.ShouldBe(InvocationOutcome.Succeeded);
            result.OutcomeRecorded.ShouldBeTrue();
            connector.Calls.ShouldBe(1);
            (await actor.GetInvocationAsync(id, token)).ShouldNotBeNull();
            (await actor.InvokeAsync(invocation, token)).IsReplay.ShouldBeTrue();
            connector.Calls.ShouldBe(1);
        }
        finally
        {
            await actor.DisconnectAsync();
        }
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Weave.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Run the sample check from a Weave checkout.");
    }

    // Observes the real connector boundary; every operation is forwarded unchanged.
    private sealed class ObservedConnector(IToolConnector inner) : IToolConnector
    {
        public int Calls { get; private set; }
        public ToolType ToolType => inner.ToolType;
        public ToolInvocation NormalizeInvocation(ToolInvocation invocation) => inner.NormalizeInvocation(invocation);
        public Task<ToolHandle> ConnectAsync(ToolSpec tool, CapabilityToken token, CancellationToken ct = default) =>
            inner.ConnectAsync(tool, token, ct);
        public Task DisconnectAsync(ToolHandle handle, CancellationToken ct = default) => inner.DisconnectAsync(handle, ct);
        public Task<ToolSchema> DiscoverSchemaAsync(ToolHandle handle, CancellationToken ct = default) =>
            inner.DiscoverSchemaAsync(handle, ct);
        public Task<ToolResult> InvokeAsync(ToolHandle handle, ToolInvocation invocation, CancellationToken ct = default)
        {
            Calls++;
            return inner.InvokeAsync(handle, invocation, ct);
        }
    }
}
