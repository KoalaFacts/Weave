namespace Weave.Cli.Tests;

public sealed class CliSystemConfigBindingTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("/fixture/host", "/fixture/host")]
    public void Load_Config_MapsSettingsAndNormalizesEmptySiloPath(string? configuredPath, string? expectedPath)
    {
        var store = new RecordingConfigStore(new CliConfig
        {
            Version = "fixture-version",
            DefaultPort = 4321,
            Storage = "sqlite",
            AuthMode = "bearer",
            RequireHttps = true,
            SiloPath = configuredPath
        });

        var snapshot = new CliSystemConfigSource(store).Load();

        snapshot.Version.ShouldBe("fixture-version");
        snapshot.DefaultPort.ShouldBe(4321);
        snapshot.Storage.ShouldBe("sqlite");
        snapshot.AuthMode.ShouldBe("bearer");
        snapshot.RequireHttps.ShouldBeTrue();
        snapshot.SiloPath.ShouldBe(expectedPath);
        snapshot.BaseUrl.ShouldBe(CliApiHttp.ResolveBaseUrl());
        snapshot.WeaveHome.ShouldBe(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave"));
    }

    [Theory]
    [InlineData("siloPath", "/fixture/new-host")]
    [InlineData("defaultPort", "4321")]
    public async Task SetAsync_WritableSetting_PreservesUnrelatedConfiguration(string key, string value)
    {
        var original = new CliConfig
        {
            DefaultPort = 1234,
            SiloPath = "/fixture/original",
            Storage = "sqlite",
            AuthMode = "bearer",
            ConnectionString = "env:FIXTURE_CONNECTION",
            AuthSecret = "env:FIXTURE_SECRET",
            RequireHttps = true
        };
        var store = new RecordingConfigStore(original);

        await new CliSystemConfigWriter(store).SetAsync(key, value, TestContext.Current.CancellationToken);

        store.Saved.ShouldNotBeNull().ShouldBe(key == "siloPath"
            ? original with { SiloPath = value }
            : original with { DefaultPort = 4321 });
        original.SiloPath.ShouldBe("/fixture/original");
        original.DefaultPort.ShouldBe(1234);
    }

    [Fact]
    public async Task SetAsync_UnknownKey_RejectsWithoutSaving()
    {
        var store = new RecordingConfigStore(new CliConfig());
        var writer = new CliSystemConfigWriter(store);

        await Should.ThrowAsync<ArgumentException>(() => writer.SetAsync("authMode", "none", TestContext.Current.CancellationToken));

        store.Saved.ShouldBeNull();
    }

    [Fact]
    public async Task SetAsync_Cancelled_DoesNotReadOrWriteStore()
    {
        var store = new RecordingConfigStore(new CliConfig());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => new CliSystemConfigWriter(store)
            .SetAsync("defaultPort", "4321", cancellation.Token));

        store.Reads.ShouldBe(0);
        store.Saved.ShouldBeNull();
    }

    private sealed class RecordingConfigStore(CliConfig config) : IConfigStore
    {
        public CliConfig? Saved { get; private set; }
        public int Reads { get; private set; }
        public CliConfig Load()
        {
            Reads++;
            return config;
        }
        public void Save(CliConfig value) => Saved = value;
        public bool Exists() => true;
    }
}
