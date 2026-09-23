using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Weave.Shared.Plugins;
using Weave.Silo.Plugins;
using Weave.Silo.Security;
using Weave.Workspaces.Manifest;

namespace Weave.Silo.Tests.Security;

public sealed class ApiAuthFailClosedTests
{
    [Theory]
    [InlineData("apikey", null)]
    [InlineData("bearer", "")]
    [InlineData("bearer", "   ")]
    [InlineData("api-key-typo", "value")]
    [InlineData("apikey", "vault:unimplemented/reference")]
    public void FromConfiguration_UnusableAuthentication_Throws(string mode, string? secret)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Weave:Auth:Mode"] = mode,
            ["Weave:Auth:Secret"] = secret
        }).Build();

        Should.Throw<InvalidOperationException>(() => ApiAuthOptions.FromConfiguration(configuration));
    }

    [Fact]
    public void FromConfiguration_MissingEnvironmentSecret_Throws()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Weave:Auth:Mode"] = "apikey",
            ["Weave:Auth:Secret"] = "env:WEAVE_UNSET_" + Guid.NewGuid().ToString("N")
        }).Build();

        Should.Throw<InvalidOperationException>(() => ApiAuthOptions.FromConfiguration(configuration));
    }

    [Fact]
    public async Task InvokeAsync_RequiredProviderMissing_DoesNotReachEndpoint()
    {
        var broker = new PluginServiceBroker(NullLogger<PluginServiceBroker>.Instance);
        var reached = false;
        var middleware = new ApiAuthMiddleware(_ => { reached = true; return Task.CompletedTask; },
            broker, new ApiAuthOptions { Mode = "apikey" }, NullLogger<ApiAuthMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/workspaces";
        using var body = new MemoryStream();
        context.Response.Body = body;

        await middleware.InvokeAsync(context);

        reached.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_AuthenticationPluginUnavailable_DoesNotReachEndpoint(bool disconnect)
    {
        var broker = new PluginServiceBroker(NullLogger<PluginServiceBroker>.Instance);
        var connector = new AuthPluginConnector(broker, NullLoggerFactory.Instance);
        var definition = new PluginDefinition
        {
            Type = "auth",
            Config = new Dictionary<string, string> { ["provider"] = "apikey" }
        };
        if (disconnect)
        {
            definition.Config["secret"] = "test-provider-secret";
            (await connector.ConnectAsync("auth", definition)).IsConnected.ShouldBeTrue();
            await connector.DisconnectAsync("auth");
        }
        else
        {
            (await connector.ConnectAsync("auth", definition)).IsConnected.ShouldBeFalse();
        }

        var reached = false;
        var middleware = new ApiAuthMiddleware(_ => { reached = true; return Task.CompletedTask; },
            broker, new ApiAuthOptions { Mode = "none" }, NullLogger<ApiAuthMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/workspaces";
        using var body = new MemoryStream();
        context.Response.Body = body;

        await middleware.InvokeAsync(context);

        reached.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        connector.GetStatus("auth").IsConnected.ShouldBeFalse();
    }

    [Fact]
    public async Task ConnectAsync_FailedReplacement_PreservesWorkingAuthentication()
    {
        var broker = new PluginServiceBroker(NullLogger<PluginServiceBroker>.Instance);
        var connector = new AuthPluginConnector(broker, NullLoggerFactory.Instance);
        var configured = new PluginDefinition
        {
            Type = "auth",
            Config = new Dictionary<string, string> { ["provider"] = "apikey", ["secret"] = "test-working-secret" }
        };
        await connector.ConnectAsync("auth", configured);
        configured.Config.Remove("secret");

        (await connector.ConnectAsync("auth", configured)).IsConnected.ShouldBeFalse();
        var provider = broker.Get<IApiAuthProvider>();
        provider.ShouldNotBeNull();
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = "test-working-secret";
        (await provider.AuthenticateAsync(context)).ShouldBeTrue();
        context.Request.Headers.Remove("X-Api-Key");
        (await provider.AuthenticateAsync(context)).ShouldBeFalse();
    }
}
