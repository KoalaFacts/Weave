using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Security.Actors;
using Weave.Security.Scanning;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;
using Weave.Tools.Connectors;
using Weave.Tools.Discovery;
using Weave.Tools.Tool;

namespace Weave.Tools.Tests;

public sealed class ToolConnectionAuthorityTests
{
    [Fact]
    public async Task ConnectAsync_ReplaceHttpEndpoint_PreservesNewConnectionAuthentication()
    {
        using var fx = new Fixture();
        await fx.Actor.ConnectAsync(fx.Spec("https://old.test", "Bearer old"), fx.Token);
        await fx.Actor.ConnectAsync(fx.Spec("https://current.test", "Bearer current"), fx.Token);

        var result = await fx.Actor.InvokeAsync(fx.Invocation(), fx.Token);

        result.Success.ShouldBeTrue(result.Error);
        fx.Handler.Target.ShouldBe("https://current.test/echo");
        fx.Handler.Authorization.ShouldBe("Bearer current");
    }

    [Fact]
    public async Task InvokeAsync_DisconnectedDuringSecretLookup_DoesNotSendHttpRequest()
    {
        using var fx = new Fixture();
        await fx.Actor.ConnectAsync(fx.Spec("https://current.test", "Bearer current"), fx.Token);
        fx.Proxy.SubstituteAsync(Arg.Any<string>()).Returns(async ci =>
        {
            await fx.Actor.DisconnectAsync();
            return ci.Arg<string>();
        });

        await Should.ThrowAsync<InvalidOperationException>(() => fx.Actor.InvokeAsync(fx.Invocation(), fx.Token));

        fx.Handler.Requests.ShouldBe(0);
    }

    [Fact]
    public async Task InvokeAsync_ReconnectedDuringSecretLookup_DoesNotRetargetHttpRequest()
    {
        using var fx = new Fixture();
        await fx.Actor.ConnectAsync(fx.Spec("https://old.test", "Bearer old"), fx.Token);
        fx.Proxy.SubstituteAsync(Arg.Any<string>()).Returns(async ci =>
        {
            await fx.Actor.ConnectAsync(fx.Spec("https://replacement.test", "Bearer replacement"), fx.Token);
            return ci.Arg<string>();
        });

        await Should.ThrowAsync<InvalidOperationException>(() => fx.Actor.InvokeAsync(fx.Invocation(), fx.Token));

        fx.Handler.Requests.ShouldBe(0);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"weave-connection-{Guid.NewGuid():N}");
        private readonly HttpClient _client;
        private readonly string _name = "api";
        public RecordingHandler Handler { get; } = new();
        public ISecretProxyActor Proxy { get; } = Substitute.For<ISecretProxyActor>();
        public ToolActor Actor { get; }
        public CapabilityToken Token { get; }

        public Fixture()
        {
            _client = new HttpClient(Handler);
            var connector = new DirectHttpToolConnector(_client, NullLogger<DirectHttpToolConnector>.Instance);
            var tokens = new CapabilityTokenService(Options.Create(new CapabilityTokenOptions
            {
                SigningKey = "test-signing-key-that-is-at-least-32-chars-long",
                RevocationDirectory = _directory
            }), TimeProvider.System);
            Token = tokens.Mint(new CapabilityTokenRequest
            {
                WorkspaceId = "workspace-a", IssuedTo = "operator", Grants = ["tool:*"], Lifetime = TimeSpan.FromMinutes(5)
            });
            var actors = Substitute.For<IVirtualActorProvider>();
            Proxy.SubstituteAsync(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
            actors.GetActor<ISecretProxyActor>(Arg.Any<VirtualActorId>()).Returns(Proxy);
            var events = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
            Actor = new ToolActor(actors,
                new ToolDiscoveryService([connector], NullLogger<ToolDiscoveryService>.Instance),
                new LeakScanner(NullLogger<LeakScanner>.Instance),
                new CapabilityAuthorizer(tokens, events, NullLogger<CapabilityAuthorizer>.Instance),
                new LifecycleManager(NullLogger<LifecycleManager>.Instance), events, NullLogger<ToolActor>.Instance);
        }

        public ToolSpec Spec(string endpoint, string header) => new()
        {
            Name = _name, Type = ToolType.DirectHttp,
            DirectHttp = new DirectHttpToolConfig { BaseUrl = endpoint, AuthHeader = header }
        };

        public ToolInvocation Invocation() => new() { ToolName = _name, Method = "echo", Parameters = new() { ["message"] = "hello" } };

        public void Dispose()
        {
            _client.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? Target { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Target = request.RequestUri?.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        }
    }
}
