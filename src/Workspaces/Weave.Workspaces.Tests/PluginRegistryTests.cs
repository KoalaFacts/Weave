using Microsoft.Extensions.Logging.Abstractions;
using Weave.Workspaces.Models;
using Weave.Workspaces.Plugins;

namespace Weave.Workspaces.Tests;

public sealed class PluginRegistryTests
{
    private static readonly PluginSchema TestSchema = new()
    {
        Type = "test",
        Description = "Test plugin",
        Provides = ["test"],
        Config =
        [
            new() { Name = "port", Description = "Port number" },
        ]
    };

    private static readonly PluginSchema VaultLikeSchema = new()
    {
        Type = "vault",
        Description = "Vault-like",
        Provides = ["secrets"],
        Config =
        [
            new() { Name = "address", Description = "Server address", Required = true, EnvVar = "VAULT_ADDR" },
            new() { Name = "token", Description = "Auth token", Secret = true },
        ]
    };

    private static PluginRegistry CreateRegistry(params IPluginConnector[] connectors) =>
        new(connectors, NullLogger<PluginRegistry>.Instance);

    [Fact]
    public async Task ConnectAsync_KnownType_ReturnsConnected()
    {
        var connector = new FakePluginConnector("dapr", connected: true, schema: TestSchema);
        var registry = CreateRegistry(connector);

        var status = await registry.ConnectAsync("my-dapr", new PluginDefinition
        {
            Type = "dapr",
            Config = new Dictionary<string, string> { ["port"] = "3500" }
        });

        status.IsConnected.ShouldBeTrue();
        status.Name.ShouldBe("my-dapr");
        status.Type.ShouldBe("dapr");
    }

    [Fact]
    public async Task ConnectAsync_UnknownType_ReturnsError()
    {
        var registry = CreateRegistry();

        var status = await registry.ConnectAsync("mystery", new PluginDefinition { Type = "alien" });

        status.IsConnected.ShouldBeFalse();
        status.Error!.ShouldContain("alien");
    }

    [Fact]
    public async Task ConnectAllAsync_RegistersMultiple()
    {
        var dapr = new FakePluginConnector("dapr", connected: true, schema: TestSchema);
        var vault = new FakePluginConnector("vault", connected: true, schema: VaultLikeSchema);
        var registry = CreateRegistry(dapr, vault);

        var plugins = new Dictionary<string, PluginDefinition>
        {
            ["sidecar"] = new PluginDefinition { Type = "dapr" },
            ["secrets"] = new PluginDefinition
            {
                Type = "vault",
                Config = new Dictionary<string, string> { ["address"] = "http://localhost:8200" }
            }
        };

        var results = await registry.ConnectAllAsync(plugins);

        results.Count.ShouldBe(2);
        results.ShouldAllBe(s => s.IsConnected);
    }

    [Fact]
    public async Task DisconnectAsync_ActivePlugin_RemovesIt()
    {
        var connector = new FakePluginConnector("dapr", connected: true, schema: TestSchema);
        var registry = CreateRegistry(connector);

        await registry.ConnectAsync("dapr-1", new PluginDefinition { Type = "dapr" });
        registry.GetAll().Count.ShouldBe(1);

        var status = await registry.DisconnectAsync("dapr-1");
        status.IsConnected.ShouldBeFalse();
        registry.GetAll().ShouldBeEmpty();
    }

    [Fact]
    public async Task DisconnectAsync_UnknownPlugin_ReturnsError()
    {
        var registry = CreateRegistry();

        var status = await registry.DisconnectAsync("nonexistent");

        status.IsConnected.ShouldBeFalse();
        status.Error!.ShouldContain("not active");
    }

    [Fact]
    public async Task GetAll_ReturnsAllActive()
    {
        var connector = new FakePluginConnector("http", connected: true, schema: TestSchema);
        var registry = CreateRegistry(connector);

        await registry.ConnectAsync("api-1", new PluginDefinition { Type = "http" });
        await registry.ConnectAsync("api-2", new PluginDefinition { Type = "http" });

        registry.GetAll().Count.ShouldBe(2);
    }

    [Fact]
    public async Task ConnectAsync_AlreadyActive_HotSwapsPlugin()
    {
        var connector = new FakePluginConnector("dapr", connected: true, schema: TestSchema);
        var registry = CreateRegistry(connector);

        await registry.ConnectAsync("my-dapr", new PluginDefinition
        {
            Type = "dapr",
            Config = new Dictionary<string, string> { ["port"] = "3500" }
        });

        await registry.ConnectAsync("my-dapr", new PluginDefinition
        {
            Type = "dapr",
            Config = new Dictionary<string, string> { ["port"] = "3501" }
        });

        registry.GetAll().Count.ShouldBe(1);
        connector.DisconnectCount.ShouldBe(1);
    }

    [Fact]
    public async Task ConnectAsync_AlreadyActive_DifferentType_HotSwaps()
    {
        var dapr = new FakePluginConnector("dapr", connected: true, schema: TestSchema);
        var webhook = new FakePluginConnector("webhook", connected: true, schema: TestSchema);
        var registry = CreateRegistry(dapr, webhook);

        await registry.ConnectAsync("events", new PluginDefinition { Type = "dapr" });
        await registry.ConnectAsync("events", new PluginDefinition { Type = "webhook" });

        registry.GetAll().Count.ShouldBe(1);
        registry.GetAll()[0].Type.ShouldBe("webhook");
        dapr.DisconnectCount.ShouldBe(1);
    }

    [Fact]
    public async Task ConnectAsync_ConnectorThrows_ReturnsError()
    {
        var connector = new ThrowingPluginConnector("dapr");
        var registry = CreateRegistry(connector);

        var status = await registry.ConnectAsync("bad", new PluginDefinition { Type = "dapr" });

        status.IsConnected.ShouldBeFalse();
        status.Error!.ShouldContain("boom");
    }

    [Fact]
    public async Task ConnectAllAsync_MixedResults_ContinuesAfterFailure()
    {
        var working = new FakePluginConnector("dapr", connected: true, schema: TestSchema);
        // "vault" has no connector registered, so it fails
        var registry = CreateRegistry(working);

        var plugins = new Dictionary<string, PluginDefinition>
        {
            ["good"] = new PluginDefinition { Type = "dapr" },
            ["bad"] = new PluginDefinition { Type = "vault" },
        };

        var results = await registry.ConnectAllAsync(plugins);

        results.Count.ShouldBe(2);
        results[0].IsConnected.ShouldBeTrue();
        results[1].IsConnected.ShouldBeFalse();
        // "good" should be in active, "bad" should not
        registry.GetAll().Count.ShouldBe(1);
    }

    // --- Make-before-break hot-swap ---

    [Fact]
    public async Task HotSwap_MakeBeforeBreak_FailedNewLeaveOldRunning()
    {
        var working = new FakePluginConnector("dapr", connected: true, schema: TestSchema);
        var failing = new FakePluginConnector("webhook", connected: false, schema: TestSchema);
        var registry = CreateRegistry(working, failing);

        // Connect working plugin
        var first = await registry.ConnectAsync("events", new PluginDefinition { Type = "dapr" });
        first.IsConnected.ShouldBeTrue();

        // Try to hot-swap with a connector that reports IsConnected = false
        var second = await registry.ConnectAsync("events", new PluginDefinition { Type = "webhook" });
        second.IsConnected.ShouldBeFalse();

        // Old plugin should NOT have been disconnected (make-before-break)
        working.DisconnectCount.ShouldBe(0);
    }

    [Fact]
    public async Task HotSwap_MakeBeforeBreak_ThrowingNewLeavesOldRunning()
    {
        var working = new FakePluginConnector("dapr", connected: true, schema: TestSchema);
        var throwing = new ThrowingPluginConnector("webhook");
        var registry = CreateRegistry(working, throwing);

        await registry.ConnectAsync("events", new PluginDefinition { Type = "dapr" });

        var result = await registry.ConnectAsync("events", new PluginDefinition { Type = "webhook" });
        result.IsConnected.ShouldBeFalse();

        // Old plugin still active — never disconnected, still in registry
        working.DisconnectCount.ShouldBe(0);
        var active = registry.GetAll();
        active.Count.ShouldBe(1);
        active[0].Type.ShouldBe("dapr");
        active[0].IsConnected.ShouldBeTrue();
    }

    // --- Schema validation tests ---

    [Fact]
    public async Task ConnectAsync_MissingRequiredConfig_ReturnsError()
    {
        var connector = new FakePluginConnector("vault", connected: true, schema: VaultLikeSchema);
        var registry = CreateRegistry(connector);

        // Missing required "address"
        var status = await registry.ConnectAsync("secrets", new PluginDefinition { Type = "vault" });

        status.IsConnected.ShouldBeFalse();
        status.Error!.ShouldContain("address");
    }

    [Fact]
    public async Task ConnectAsync_SecretFieldRedactedInStatus()
    {
        var connector = new FakePluginConnector("vault", connected: true, schema: VaultLikeSchema,
            infoOnConnect: new Dictionary<string, string>
            {
                ["address"] = "http://vault:8200",
                ["token"] = "s.supersecret"
            });
        var registry = CreateRegistry(connector);

        var status = await registry.ConnectAsync("secrets", new PluginDefinition
        {
            Type = "vault",
            Config = new Dictionary<string, string> { ["address"] = "http://vault:8200" }
        });

        status.IsConnected.ShouldBeTrue();
        status.Info["address"].ShouldBe("http://vault:8200");
        status.Info["token"].ShouldBe("***");
    }

    [Fact]
    public void GetCatalog_ReturnsAllSchemas()
    {
        var dapr = new FakePluginConnector("dapr", connected: true, schema: TestSchema);
        var vault = new FakePluginConnector("vault", connected: true, schema: VaultLikeSchema);
        var registry = CreateRegistry(dapr, vault);

        var catalog = registry.GetCatalog();

        catalog.Count.ShouldBe(2);
        catalog.ShouldContain(s => s.Type == "test");
        catalog.ShouldContain(s => s.Type == "vault");
    }

    // --- ResolveConfig / ValidateConfig unit tests ---

    [Fact]
    public void ResolveConfig_FillsDefaultValues()
    {
        var schema = new PluginSchema
        {
            Type = "test",
            Description = "test",
            Provides = [],
            Config = [new() { Name = "timeout", Description = "Timeout", Default = "30" }]
        };

        var def = new PluginDefinition { Type = "test" };
        var resolved = PluginRegistry.ResolveConfig(def, schema);

        resolved.Config["timeout"].ShouldBe("30");
    }

    [Fact]
    public void ResolveConfig_ExplicitValueOverridesDefault()
    {
        var schema = new PluginSchema
        {
            Type = "test",
            Description = "test",
            Provides = [],
            Config = [new() { Name = "timeout", Description = "Timeout", Default = "30" }]
        };

        var def = new PluginDefinition
        {
            Type = "test",
            Config = new Dictionary<string, string> { ["timeout"] = "60" }
        };
        var resolved = PluginRegistry.ResolveConfig(def, schema);

        resolved.Config["timeout"].ShouldBe("60");
    }

    [Fact]
    public void ResolveConfig_FillsFromEnvironmentVariable()
    {
        var envKey = $"WEAVE_TEST_{Guid.NewGuid():N}";
        try
        {
            Environment.SetEnvironmentVariable(envKey, "from-env");
            var schema = new PluginSchema
            {
                Type = "test",
                Description = "test",
                Provides = [],
                Config = [new() { Name = "addr", Description = "Address", EnvVar = envKey }]
            };

            var def = new PluginDefinition { Type = "test" };
            var resolved = PluginRegistry.ResolveConfig(def, schema);

            resolved.Config["addr"].ShouldBe("from-env");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, null);
        }
    }

    [Fact]
    public void ResolveConfig_ExplicitValueOverridesEnvVar()
    {
        var envKey = $"WEAVE_TEST_{Guid.NewGuid():N}";
        try
        {
            Environment.SetEnvironmentVariable(envKey, "from-env");
            var schema = new PluginSchema
            {
                Type = "test",
                Description = "test",
                Provides = [],
                Config = [new() { Name = "addr", Description = "Address", EnvVar = envKey }]
            };

            var def = new PluginDefinition
            {
                Type = "test",
                Config = new Dictionary<string, string> { ["addr"] = "explicit" }
            };
            var resolved = PluginRegistry.ResolveConfig(def, schema);

            resolved.Config["addr"].ShouldBe("explicit");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envKey, null);
        }
    }

    [Fact]
    public void ValidateConfig_RequiredFieldMissing_ReturnsError()
    {
        var schema = new PluginSchema
        {
            Type = "test",
            Description = "test",
            Provides = [],
            Config = [new() { Name = "url", Description = "URL", Required = true }]
        };

        var def = new PluginDefinition { Type = "test" };
        var error = PluginRegistry.ValidateConfig(def, schema);

        error.ShouldNotBeNull();
        error.ShouldContain("url");
    }

    [Fact]
    public void ValidateConfig_RequiredFieldPresent_ReturnsNull()
    {
        var schema = new PluginSchema
        {
            Type = "test",
            Description = "test",
            Provides = [],
            Config = [new() { Name = "url", Description = "URL", Required = true }]
        };

        var def = new PluginDefinition
        {
            Type = "test",
            Config = new Dictionary<string, string> { ["url"] = "http://localhost" }
        };
        var error = PluginRegistry.ValidateConfig(def, schema);

        error.ShouldBeNull();
    }

    [Fact]
    public void ValidateConfig_RequiredFieldWithEnvVar_ShowsHint()
    {
        var schema = new PluginSchema
        {
            Type = "test",
            Description = "test",
            Provides = [],
            Config = [new() { Name = "addr", Description = "Address", Required = true, EnvVar = "MY_ADDR" }]
        };

        var def = new PluginDefinition { Type = "test" };
        var error = PluginRegistry.ValidateConfig(def, schema);

        error.ShouldNotBeNull();
        error.ShouldContain("MY_ADDR");
    }

    // --- Fakes ---

    private sealed class FakePluginConnector(
        string type,
        bool connected,
        PluginSchema? schema = null,
        Dictionary<string, string>? infoOnConnect = null) : IPluginConnector
    {
        public string PluginType => type;
        public int DisconnectCount { get; private set; }

        public PluginSchema Schema { get; } = schema ?? new()
        {
            Type = type,
            Description = $"Fake {type}",
            Provides = [],
            Config = []
        };

        public Task<PluginStatus> ConnectAsync(string name, PluginDefinition definition) =>
            Task.FromResult<PluginStatus>(new()
            {
                Name = name,
                Type = type,
                IsConnected = connected,
                Info = infoOnConnect ?? new Dictionary<string, string>()
            });

        public Task<PluginStatus> DisconnectAsync(string name)
        {
            DisconnectCount++;
            return Task.FromResult<PluginStatus>(new() { Name = name, Type = type, IsConnected = false });
        }

        public PluginStatus GetStatus(string name) =>
            new() { Name = name, Type = type, IsConnected = connected };
    }

    private sealed class ThrowingPluginConnector(string type) : IPluginConnector
    {
        public string PluginType => type;

        public PluginSchema Schema { get; } = new()
        {
            Type = type,
            Description = $"Throwing {type}",
            Provides = [],
            Config = []
        };

        public Task<PluginStatus> ConnectAsync(string name, PluginDefinition definition) =>
            throw new InvalidOperationException("boom");

        public Task<PluginStatus> DisconnectAsync(string name) =>
            Task.FromResult<PluginStatus>(new() { Name = name, Type = type, IsConnected = false });

        public PluginStatus GetStatus(string name) =>
            new() { Name = name, Type = type, IsConnected = false };
    }
}
