using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Weave.Security.Tokens;
using Weave.Shared.Events;
using Weave.Shared.Plugins;
using Weave.Silo.Events;
using Weave.Silo.Plugins;
using Weave.Tools.Discovery;
using Weave.Workspaces.Models;

namespace Weave.Silo.Tests;

/// <summary>
/// Unit coverage for the Silo's plugin connectors. Each connector is
/// a thin adapter that validates config, swaps a service via
/// <see cref="PluginServiceBroker"/>, and returns a <see cref="PluginStatus"/>.
/// The HTTP machinery isn't exercised here — that's integration territory —
/// but every config-validation branch is.
/// </summary>
public static class PluginConnectorTests
{
    // ── Shared helpers ────────────────────────────────────────────

    private static IHttpClientFactory CreateHttpClientFactory()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    private static NullLoggerFactory Loggers() => NullLoggerFactory.Instance;

    private static PluginServiceBroker CreateBroker() =>
        new(NullLogger<PluginServiceBroker>.Instance);

    // ── Webhook ────────────────────────────────────────────────────

    public sealed class Webhook
    {
        [Fact]
        public async Task Schema_advertises_url_as_required()
        {
            var connector = new WebhookPluginConnector(
                CreateBroker(),
                CreateHttpClientFactory(),
                Loggers());

            connector.PluginType.ShouldBe("webhook");
            connector.Schema.Provides.ShouldContain("events");
            connector.Schema.Config.ShouldContain(c => c.Name == "url" && c.Required);
            await Task.CompletedTask;
        }

        [Fact]
        public async Task ConnectAsync_without_url_returns_error_status()
        {
            var connector = new WebhookPluginConnector(
                CreateBroker(),
                CreateHttpClientFactory(),
                Loggers());

            var status = await connector.ConnectAsync("wh1", new PluginDefinition { Type = "webhook" });

            status.IsConnected.ShouldBeFalse();
            status.Error!.ShouldContain("url");
        }

        [Fact]
        public async Task ConnectAsync_with_invalid_url_returns_error_status()
        {
            var connector = new WebhookPluginConnector(
                CreateBroker(),
                CreateHttpClientFactory(),
                Loggers());

            var def = new PluginDefinition
            {
                Type = "webhook",
                Config = new Dictionary<string, string> { ["url"] = "not-a-url" }
            };

            var status = await connector.ConnectAsync("wh1", def);

            status.IsConnected.ShouldBeFalse();
            status.Error!.ShouldContain("Invalid webhook URL");
        }

        [Fact]
        public async Task ConnectAsync_with_valid_url_swaps_event_bus_and_returns_connected()
        {
            var broker = CreateBroker();
            var connector = new WebhookPluginConnector(broker, CreateHttpClientFactory(), Loggers());

            var def = new PluginDefinition
            {
                Type = "webhook",
                Config = new Dictionary<string, string> { ["url"] = "https://example.com/hook" }
            };

            var status = await connector.ConnectAsync("wh1", def);

            status.IsConnected.ShouldBeTrue();
            status.Info["url"].ShouldBe("https://example.com/hook");
            broker.Get<IEventBus>().ShouldBeOfType<WebhookEventBus>();
        }

        [Fact]
        public async Task DisconnectAsync_restores_default_event_bus()
        {
            var broker = CreateBroker();
            var connector = new WebhookPluginConnector(broker, CreateHttpClientFactory(), Loggers());

            await connector.ConnectAsync("wh1", new PluginDefinition
            {
                Type = "webhook",
                Config = new Dictionary<string, string> { ["url"] = "https://example.com/hook" }
            });
            var status = await connector.DisconnectAsync("wh1");

            status.IsConnected.ShouldBeFalse();
            // The broker should no longer hold a WebhookEventBus.
            (broker.Get<IEventBus>() is WebhookEventBus).ShouldBeFalse();
        }

        [Fact]
        public async Task GetStatus_reflects_whether_bus_is_swapped()
        {
            var broker = CreateBroker();
            var connector = new WebhookPluginConnector(broker, CreateHttpClientFactory(), Loggers());

            connector.GetStatus("wh1").IsConnected.ShouldBeFalse();

            await connector.ConnectAsync("wh1", new PluginDefinition
            {
                Type = "webhook",
                Config = new Dictionary<string, string> { ["url"] = "https://example.com/hook" }
            });

            connector.GetStatus("wh1").IsConnected.ShouldBeTrue();
        }
    }

    // ── Vault ──────────────────────────────────────────────────────

    public sealed class Vault
    {
        private static CapabilityTokenService CreateTokenService() =>
            new(Options.Create(
                new CapabilityTokenOptions { SigningKey = "test-signing-key-at-least-32-chars-long!" }),
                TimeProvider.System);

        [Fact]
        public void Schema_advertises_address_as_required()
        {
            var connector = new VaultPluginConnector(
                CreateBroker(),
                CreateHttpClientFactory(),
                CreateTokenService(),
                Loggers());

            connector.PluginType.ShouldBe("vault");
            connector.Schema.Provides.ShouldContain("secrets");
            connector.Schema.Config.ShouldContain(c => c.Name == "address" && c.Required);
        }

        [Fact]
        public async Task ConnectAsync_without_address_returns_error_status()
        {
            var connector = new VaultPluginConnector(
                CreateBroker(),
                CreateHttpClientFactory(),
                CreateTokenService(),
                Loggers());

            var status = await connector.ConnectAsync("v1", new PluginDefinition { Type = "vault" });

            status.IsConnected.ShouldBeFalse();
            status.Error!.ShouldContain("address");
        }

        [Fact]
        public async Task ConnectAsync_with_address_but_no_token_still_connects()
        {
            var broker = CreateBroker();
            var connector = new VaultPluginConnector(
                broker, CreateHttpClientFactory(), CreateTokenService(), Loggers());

            var def = new PluginDefinition
            {
                Type = "vault",
                Config = new Dictionary<string, string> { ["address"] = "http://localhost:8200" }
            };

            var status = await connector.ConnectAsync("v1", def);

            status.IsConnected.ShouldBeTrue();
            status.Info["address"].ShouldBe("http://localhost:8200");
        }

        [Fact]
        public async Task GetStatus_reflects_whether_provider_is_swapped()
        {
            var broker = CreateBroker();
            var connector = new VaultPluginConnector(
                broker, CreateHttpClientFactory(), CreateTokenService(), Loggers());

            connector.GetStatus("v1").IsConnected.ShouldBeFalse();

            await connector.ConnectAsync("v1", new PluginDefinition
            {
                Type = "vault",
                Config = new Dictionary<string, string>
                {
                    ["address"] = "http://localhost:8200",
                    ["token"] = "test-token"
                }
            });

            connector.GetStatus("v1").IsConnected.ShouldBeTrue();

            await connector.DisconnectAsync("v1");
            connector.GetStatus("v1").IsConnected.ShouldBeFalse();
        }
    }

    // ── Dapr ───────────────────────────────────────────────────────

    public sealed class Dapr
    {
        [Fact]
        public void Schema_advertises_port_with_env_var_fallback()
        {
            var connector = new DaprPluginConnector(
                CreateBroker(),
                Substitute.For<IToolDiscoveryService>(),
                CreateHttpClientFactory(),
                Loggers());

            connector.PluginType.ShouldBe("dapr");
            // `port` is not marked Required — the PluginRegistry can
            // resolve it from the DAPR_HTTP_PORT env var.
            connector.Schema.Config.ShouldContain(c => c.Name == "port" && c.EnvVar == "DAPR_HTTP_PORT");
        }

        [Fact]
        public async Task ConnectAsync_without_port_returns_error_status()
        {
            var connector = new DaprPluginConnector(
                CreateBroker(),
                Substitute.For<IToolDiscoveryService>(),
                CreateHttpClientFactory(),
                Loggers());

            var status = await connector.ConnectAsync("d1", new PluginDefinition { Type = "dapr" });

            status.IsConnected.ShouldBeFalse();
            status.Error.ShouldNotBeNullOrWhiteSpace();
        }

        [Fact]
        public async Task ConnectAsync_with_valid_port_connects_and_disconnect_clears()
        {
            var broker = CreateBroker();
            var connector = new DaprPluginConnector(broker, Substitute.For<IToolDiscoveryService>(), CreateHttpClientFactory(), Loggers());

            var def = new PluginDefinition
            {
                Type = "dapr",
                Config = new Dictionary<string, string> { ["port"] = "3500" }
            };

            var connected = await connector.ConnectAsync("d1", def);
            connected.IsConnected.ShouldBeTrue();

            var disconnected = await connector.DisconnectAsync("d1");
            disconnected.IsConnected.ShouldBeFalse();
        }
    }

    // ── Http (generic) ─────────────────────────────────────────────

    public sealed class HttpGeneric
    {
        [Fact]
        public void Schema_advertises_base_url_as_required()
        {
            var connector = new HttpPluginConnector(
                CreateBroker(),
                CreateHttpClientFactory(),
                Loggers());

            connector.PluginType.ShouldBe("http");
            connector.Schema.Config.ShouldContain(c => c.Name == "base_url" && c.Required);
        }

        [Fact]
        public async Task ConnectAsync_without_base_url_returns_error_status()
        {
            var connector = new HttpPluginConnector(
                CreateBroker(),
                CreateHttpClientFactory(),
                Loggers());

            var status = await connector.ConnectAsync("h1", new PluginDefinition { Type = "http" });

            status.IsConnected.ShouldBeFalse();
        }
    }

    // ── Auth ───────────────────────────────────────────────────────

    public sealed class Auth
    {
        [Fact]
        public void Schema_declares_provides_authentication()
        {
            var connector = new AuthPluginConnector(CreateBroker(), Loggers());

            connector.PluginType.ShouldBe("auth");
            connector.Schema.Provides.ShouldContain("authentication");
            connector.Schema.Config.ShouldContain(c => c.Name == "provider" && c.Required);
        }

        [Fact]
        public async Task ConnectAsync_with_unknown_provider_throws()
        {
            // The connector throws for unknown providers (including
            // an empty / missing provider name). Callers should either
            // register a provider or rely on the built-in names.
            var connector = new AuthPluginConnector(CreateBroker(), Loggers());

            await Should.ThrowAsync<InvalidOperationException>(
                () => connector.ConnectAsync("a1", new PluginDefinition { Type = "auth" }));
        }

        [Fact]
        public async Task ConnectAsync_with_apikey_provider_returns_connected()
        {
            var connector = new AuthPluginConnector(CreateBroker(), Loggers());

            var status = await connector.ConnectAsync("a1", new PluginDefinition
            {
                Type = "auth",
                Config = new Dictionary<string, string>
                {
                    ["provider"] = "apikey",
                    ["secret"] = "test-key"
                }
            });

            status.IsConnected.ShouldBeTrue();
        }

        [Fact]
        public async Task ConnectAsync_with_bearer_provider_returns_connected()
        {
            var connector = new AuthPluginConnector(CreateBroker(), Loggers());

            var status = await connector.ConnectAsync("a1", new PluginDefinition
            {
                Type = "auth",
                Config = new Dictionary<string, string>
                {
                    ["provider"] = "bearer",
                    ["secret"] = "my-token"
                }
            });

            status.IsConnected.ShouldBeTrue();
            status.Info!["provider"].ShouldBe("bearer");
        }

        [Fact]
        public async Task ConnectAsync_with_env_secret_resolves_from_environment()
        {
            const string varName = "WEAVE_TEST_AUTH_SECRET_E2E";
            Environment.SetEnvironmentVariable(varName, "env-key-value");
            try
            {
                var connector = new AuthPluginConnector(CreateBroker(), Loggers());

                var status = await connector.ConnectAsync("a1", new PluginDefinition
                {
                    Type = "auth",
                    Config = new Dictionary<string, string>
                    {
                        ["provider"] = "apikey",
                        ["secret"] = $"env:{varName}"
                    }
                });

                status.IsConnected.ShouldBeTrue();
            }
            finally
            {
                Environment.SetEnvironmentVariable(varName, null);
            }
        }

        [Fact]
        public async Task ConnectAsync_with_file_secret_resolves_from_file()
        {
            var tempFile = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(tempFile, "file-secret-value", TestContext.Current.CancellationToken);

                var connector = new AuthPluginConnector(CreateBroker(), Loggers());

                var status = await connector.ConnectAsync("a1", new PluginDefinition
                {
                    Type = "auth",
                    Config = new Dictionary<string, string>
                    {
                        ["provider"] = "apikey",
                        ["secret"] = $"file:{tempFile}"
                    }
                });

                status.IsConnected.ShouldBeTrue();
            }
            finally
            {
                File.Delete(tempFile);
            }
        }

        [Fact]
        public async Task ConnectAsync_with_missing_file_secret_for_apikey_returns_not_connected()
        {
            var connector = new AuthPluginConnector(CreateBroker(), Loggers());

            var status = await connector.ConnectAsync("a1", new PluginDefinition
            {
                Type = "auth",
                Config = new Dictionary<string, string>
                {
                    ["provider"] = "apikey",
                    ["secret"] = "file:/nonexistent/path/secret.txt"
                }
            });

            status.IsConnected.ShouldBeFalse();
            status.Error.ShouldNotBeNull();
            status.Error.ShouldContain("required");
        }

        [Fact]
        public async Task ConnectAsync_missing_secret_for_apikey_returns_not_connected()
        {
            var connector = new AuthPluginConnector(CreateBroker(), Loggers());

            var status = await connector.ConnectAsync("a1", new PluginDefinition
            {
                Type = "auth",
                Config = new Dictionary<string, string>
                {
                    ["provider"] = "apikey"
                }
            });

            status.IsConnected.ShouldBeFalse();
            status.Error.ShouldNotBeNull();
            status.Error.ShouldContain("required");
        }

        [Fact]
        public async Task ConnectAsync_missing_secret_for_bearer_returns_not_connected()
        {
            var connector = new AuthPluginConnector(CreateBroker(), Loggers());

            var status = await connector.ConnectAsync("a1", new PluginDefinition
            {
                Type = "auth",
                Config = new Dictionary<string, string>
                {
                    ["provider"] = "bearer"
                }
            });

            status.IsConnected.ShouldBeFalse();
            status.Error.ShouldNotBeNull();
            status.Error.ShouldContain("required");
        }

        [Fact]
        public async Task DisconnectAsync_clears_broker_provider()
        {
            var broker = CreateBroker();
            var connector = new AuthPluginConnector(broker, Loggers());

            await connector.ConnectAsync("a1", new PluginDefinition
            {
                Type = "auth",
                Config = new Dictionary<string, string>
                {
                    ["provider"] = "apikey",
                    ["secret"] = "test-key"
                }
            });

            var status = await connector.DisconnectAsync("a1");

            status.IsConnected.ShouldBeFalse();
        }

        [Fact]
        public async Task GetStatus_reflects_connected_provider()
        {
            var broker = CreateBroker();
            var connector = new AuthPluginConnector(broker, Loggers());

            await connector.ConnectAsync("a1", new PluginDefinition
            {
                Type = "auth",
                Config = new Dictionary<string, string>
                {
                    ["provider"] = "apikey",
                    ["secret"] = "test-key"
                }
            });

            var status = connector.GetStatus("a1");

            status.IsConnected.ShouldBeTrue();
            status.Info.ShouldNotBeNull();
            status.Info.ShouldContainKey("provider");
        }

        [Fact]
        public void GetStatus_when_not_connected_returns_disconnected()
        {
            var connector = new AuthPluginConnector(CreateBroker(), Loggers());

            var status = connector.GetStatus("a1");

            status.IsConnected.ShouldBeFalse();
            status.Info.ShouldNotBeNull();
            status.Info.ShouldBeEmpty();
        }
    }
}
