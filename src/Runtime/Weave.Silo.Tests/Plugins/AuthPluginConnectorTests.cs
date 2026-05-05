using Microsoft.Extensions.Logging.Abstractions;
using Weave.Shared.Plugins;
using Weave.Silo.Plugins;
using Weave.Silo.Security;
using Weave.Workspaces.Manifest;
namespace Weave.Silo.Tests.Plugins;

/// <summary>
/// Unit tests for <see cref="AuthPluginConnector"/> — the broker-swappable
/// authentication plugin. Covers every ConnectAsync branch (apikey / bearer /
/// unknown / missing secret / secret resolution failure), plus Disconnect
/// and GetStatus. Uses a real <see cref="PluginServiceBroker"/> — swapping
/// is the contract under test.
/// </summary>
public sealed class AuthPluginConnectorTests
{
    private static (AuthPluginConnector Connector, PluginServiceBroker Broker) CreateConnector()
    {
        var broker = new PluginServiceBroker(NullLogger<PluginServiceBroker>.Instance);
        var factory = NullLoggerFactory.Instance;
        return (new AuthPluginConnector(broker, factory), broker);
    }

    private static PluginDefinition Def(string? provider = null, string? secret = null)
    {
        var config = new Dictionary<string, string>();
        if (provider is not null)
            config["provider"] = provider;
        if (secret is not null)
            config["secret"] = secret;
        return new PluginDefinition { Type = "auth", Config = config };
    }

    [Fact]
    public void Schema_AdvertisesApiAuthFields()
    {
        var (connector, _) = CreateConnector();

        connector.PluginType.ShouldBe("auth");
        connector.Schema.Type.ShouldBe("auth");
        connector.Schema.Provides.ShouldContain("authentication");
        connector.Schema.Config.ShouldContain(f => f.Name == "provider" && f.Required);
        connector.Schema.Config.ShouldContain(f => f.Name == "secret" && f.Secret);
    }

    [Fact]
    public async Task ConnectAsync_ApiKey_WithInlineSecret_SwapsApiKeyProviderIntoBroker()
    {
        var (connector, broker) = CreateConnector();

        var status = await connector.ConnectAsync("my-auth", Def(provider: "apikey", secret: "literal-secret"));

        status.IsConnected.ShouldBeTrue();
        status.Info["provider"].ShouldBe("apikey");
        status.Info["secret"].ShouldBe("***");
        broker.Get<IApiAuthProvider>().ShouldBeOfType<ApiKeyAuthProvider>();
    }

    [Fact]
    public async Task ConnectAsync_Bearer_WithInlineSecret_SwapsBearerProvider()
    {
        var (connector, broker) = CreateConnector();

        var status = await connector.ConnectAsync("my-auth", Def(provider: "bearer", secret: "token"));

        status.IsConnected.ShouldBeTrue();
        broker.Get<IApiAuthProvider>().ShouldBeOfType<BearerAuthProvider>();
    }

    [Fact]
    public async Task ConnectAsync_ApiKey_MissingSecret_ReturnsErrorWithoutSwapping()
    {
        var (connector, broker) = CreateConnector();

        var status = await connector.ConnectAsync("my-auth", Def(provider: "apikey", secret: null));

        status.IsConnected.ShouldBeFalse();
        status.Error.ShouldNotBeNull();
        status.Error.ShouldContain("Auth secret is required");
        broker.Get<IApiAuthProvider>().ShouldBeNull();
    }

    [Fact]
    public async Task ConnectAsync_UnknownProvider_ThrowsAndLeavesBrokerUnchanged()
    {
        var (connector, broker) = CreateConnector();

        await Should.ThrowAsync<InvalidOperationException>(
            () => connector.ConnectAsync("my-auth", Def(provider: "oauth-2.0", secret: "x")));

        broker.Get<IApiAuthProvider>().ShouldBeNull();
    }

    [Fact]
    public async Task ConnectAsync_EnvSecretReference_ResolvesFromEnvironmentVariable()
    {
        var (connector, broker) = CreateConnector();
        var envVar = $"WEAVE_AUTH_PLUGIN_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(envVar, "env-value");

        try
        {
            var status = await connector.ConnectAsync("my-auth", Def(provider: "apikey", secret: $"env:{envVar}"));

            status.IsConnected.ShouldBeTrue();
            broker.Get<IApiAuthProvider>().ShouldBeOfType<ApiKeyAuthProvider>();
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    [Fact]
    public async Task ConnectAsync_FileSecretReference_ResolvesFromFile()
    {
        var (connector, broker) = CreateConnector();
        var tempPath = Path.Combine(Path.GetTempPath(), $"weave-auth-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tempPath, "file-secret-value", TestContext.Current.CancellationToken);

        try
        {
            var status = await connector.ConnectAsync("my-auth", Def(provider: "bearer", secret: $"file:{tempPath}"));

            status.IsConnected.ShouldBeTrue();
            broker.Get<IApiAuthProvider>().ShouldBeOfType<BearerAuthProvider>();
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ConnectAsync_EnvSecretReference_UnsetVar_ReturnsNotConfigured()
    {
        var (connector, broker) = CreateConnector();
        var envVar = $"WEAVE_AUTH_UNSET_{Guid.NewGuid():N}";
        // Intentionally do NOT set the env var.

        var status = await connector.ConnectAsync("my-auth", Def(provider: "apikey", secret: $"env:{envVar}"));

        status.IsConnected.ShouldBeFalse();
        status.Error.ShouldNotBeNull();
        status.Error.ShouldContain("Auth secret is required");
        broker.Get<IApiAuthProvider>().ShouldBeNull();
    }

    [Fact]
    public async Task DisconnectAsync_ClearsBrokerSlot()
    {
        var (connector, broker) = CreateConnector();
        await connector.ConnectAsync("my-auth", Def(provider: "apikey", secret: "k"));
        broker.Get<IApiAuthProvider>().ShouldNotBeNull();

        var status = await connector.DisconnectAsync("my-auth");

        status.IsConnected.ShouldBeFalse();
        broker.Get<IApiAuthProvider>().ShouldBeNull();
    }

    [Fact]
    public async Task GetStatus_NotConnected_ReturnsNotConnected()
    {
        var (connector, _) = CreateConnector();

        var status = connector.GetStatus("my-auth");

        status.IsConnected.ShouldBeFalse();
        status.Info.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task GetStatus_Connected_IncludesProviderName()
    {
        var (connector, _) = CreateConnector();
        await connector.ConnectAsync("my-auth", Def(provider: "bearer", secret: "t"));

        var status = connector.GetStatus("my-auth");

        status.IsConnected.ShouldBeTrue();
        status.Info["provider"].ShouldBe("bearer");
    }
}
