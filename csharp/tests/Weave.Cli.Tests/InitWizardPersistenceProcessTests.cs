using System.Text;

namespace Weave.Cli.Tests;

[Collection("CLI init PTY")]
[Trait("Category", "Integration")]
public sealed class InitWizardPersistenceProcessTests
{
    private const string ExistingConfig = """
        {
          "version": "1.0",
          "siloPath": "/fixture/previous-runtime",
          "defaultPort": 9511,
          "storage": "postgresql",
          "connectionString": "env:PREVIOUS_FIXTURE_CONNECTION",
          "authMode": "bearer",
          "authSecret": "env:PREVIOUS_FIXTURE_AUTH",
          "requireHttps": true,
          "preserveMarker": "untouched original field"
        }
        """;

    [Fact]
    public async Task Init_DeclineReconfiguration_PreservesOriginalConfigBytes()
    {
        using var fixture = new InitWizardProcessFixture();
        var original = Encoding.UTF8.GetBytes(ExistingConfig + "\n");
        await File.WriteAllBytesAsync(fixture.ConfigPath, original, TestContext.Current.CancellationToken);

        using var result = await fixture.RunAsync("decline");

        (await File.ReadAllBytesAsync(fixture.ConfigPath, TestContext.Current.CancellationToken)).ShouldBe(original);
        var output = result.RootElement.GetProperty("output").GetString();
        output.ShouldNotBeNull();
        output.ShouldContain("Existing configuration found:");
        output.ShouldNotContain("Step 1 · Storage");
        output.ShouldNotContain("Environment configured.");
        result.RootElement.GetProperty("stages").EnumerateArray().Select(stage => stage.GetString()).ShouldBe(["declined"]);
    }

    [Fact]
    public async Task Init_DefaultSelections_PersistsPrivateSqliteDatabaseAndDetectedRuntime()
    {
        using var fixture = new InitWizardProcessFixture();

        using var result = await fixture.RunAsync("sqlite-defaults");

        using var config = fixture.ReadConfig();
        config.RootElement.GetProperty("version").GetString().ShouldBe("1.0");
        config.RootElement.GetProperty("storage").GetString().ShouldBe("sqlite");
        config.RootElement.GetProperty("connectionString").GetString().ShouldBe($"Data Source={Path.Join(fixture.Private, "weave.db")}");
        config.RootElement.GetProperty("defaultPort").GetInt32().ShouldBe(9401);
        config.RootElement.GetProperty("siloPath").GetString().ShouldBe(fixture.DetectedRuntime);
        config.RootElement.GetProperty("authMode").GetString().ShouldBe("none");
        config.RootElement.GetProperty("requireHttps").GetBoolean().ShouldBeFalse();
        config.RootElement.TryGetProperty("authSecret", out _).ShouldBeFalse();
        File.Exists(Path.Join(fixture.Private, "weave.db")).ShouldBeFalse("Init configures storage; it must not start a runtime or create a database.");
        result.RootElement.GetProperty("stages").EnumerateArray().Select(stage => stage.GetString()).ShouldContain("detected-runtime");
    }

    [Fact]
    public async Task Init_InvalidPortTextThenExplicitMemorySettings_PersistsCorrectedPortAndSpacedRuntimePath()
    {
        using var fixture = new InitWizardProcessFixture();

        using var result = await fixture.RunAsync("memory-explicit");

        using var config = fixture.ReadConfig();
        config.RootElement.GetProperty("storage").GetString().ShouldBe("memory");
        config.RootElement.GetProperty("defaultPort").GetInt32().ShouldBe(9527);
        config.RootElement.GetProperty("siloPath").GetString().ShouldBe(fixture.ExplicitRuntime);
        config.RootElement.GetProperty("authMode").GetString().ShouldBe("none");
        config.RootElement.GetProperty("requireHttps").GetBoolean().ShouldBeFalse();
        config.RootElement.TryGetProperty("connectionString", out _).ShouldBeFalse();
        result.RootElement.GetProperty("stages").EnumerateArray().Select(stage => stage.GetString()).ShouldContain("invalid-port-rejected");
        var output = result.RootElement.GetProperty("output").GetString();
        output.ShouldNotBeNull();
        output.ShouldContain("Invalid input");
        (await File.ReadAllTextAsync(fixture.ExplicitRuntime, TestContext.Current.CancellationToken))
            .ShouldBe("fixture runtime path; init must not execute this file");
    }

    [Fact]
    public async Task Init_AcceptReconfigurationAndDeferRuntime_ReplacesPriorStorageAndAuthReferences()
    {
        using var fixture = new InitWizardProcessFixture();
        await File.WriteAllTextAsync(fixture.ConfigPath, ExistingConfig, TestContext.Current.CancellationToken);

        using var result = await fixture.RunAsync("reconfigure");

        using var config = fixture.ReadConfig();
        config.RootElement.GetProperty("storage").GetString().ShouldBe("memory");
        config.RootElement.GetProperty("defaultPort").GetInt32().ShouldBe(9531);
        config.RootElement.GetProperty("authMode").GetString().ShouldBe("none");
        config.RootElement.GetProperty("requireHttps").GetBoolean().ShouldBeFalse();
        config.RootElement.TryGetProperty("siloPath", out _).ShouldBeFalse();
        config.RootElement.TryGetProperty("connectionString", out _).ShouldBeFalse();
        config.RootElement.TryGetProperty("authSecret", out _).ShouldBeFalse();
        result.RootElement.GetProperty("stages").EnumerateArray().Select(stage => stage.GetString()).ShouldContain("runtime-declined");
    }

    [Fact]
    public async Task Init_BearerEnvironmentReferenceAndHttps_PersistsReferenceWithoutFixtureSecret()
    {
        using var fixture = new InitWizardProcessFixture();

        using var result = await fixture.RunAsync("bearer-env");

        using var config = fixture.ReadConfig();
        config.RootElement.GetProperty("authMode").GetString().ShouldBe("bearer");
        config.RootElement.GetProperty("authSecret").GetString().ShouldBe("env:WEAVE_API_SECRET");
        config.RootElement.GetProperty("requireHttps").GetBoolean().ShouldBeTrue();
        config.RootElement.GetRawText().ShouldNotContain("fixture-auth-environment-sentinel");
        var output = result.RootElement.GetProperty("output").GetString();
        output.ShouldNotBeNull();
        output.ShouldContain("WEAVE_API_SECRET is set.");
        output.ShouldNotContain("fixture-auth-environment-sentinel");
    }

    [Fact]
    public async Task Init_ApiKeyFileReference_PreservesSecretFileAndStoresReferenceOnly()
    {
        using var fixture = new InitWizardProcessFixture();

        using var result = await fixture.RunAsync("apikey-file");

        using var config = fixture.ReadConfig();
        var secretPath = Path.Join(fixture.Private, "api.secret");
        config.RootElement.GetProperty("authMode").GetString().ShouldBe("apikey");
        config.RootElement.GetProperty("authSecret").GetString().ShouldBe($"file:{secretPath}");
        config.RootElement.GetProperty("requireHttps").GetBoolean().ShouldBeFalse();
        config.RootElement.GetRawText().ShouldNotContain("fixture-auth-file-sentinel");
        (await File.ReadAllTextAsync(secretPath, TestContext.Current.CancellationToken)).ShouldBe("fixture-auth-file-sentinel\n");
        var output = result.RootElement.GetProperty("output").GetString();
        output.ShouldNotBeNull();
        output.ShouldNotContain("fixture-auth-file-sentinel");
    }

    [Fact]
    public async Task Init_UnsetPostgresEnvironmentReference_PersistsReferenceAndExplainsResolutionFailure()
    {
        using var fixture = new InitWizardProcessFixture();

        using var result = await fixture.RunAsync("postgres-env");

        using var config = fixture.ReadConfig();
        config.RootElement.GetProperty("storage").GetString().ShouldBe("postgresql");
        config.RootElement.GetProperty("connectionString").GetString().ShouldBe("env:WEAVE_PG_CONNECTION");
        config.RootElement.GetProperty("authMode").GetString().ShouldBe("none");
        var output = result.RootElement.GetProperty("output").GetString();
        output.ShouldNotBeNull();
        output.ShouldContain("Could not resolve connection string:");
        output.ShouldContain("Environment variable 'WEAVE_PG_CONNECTION' is not set.");
        output.ShouldNotContain("Connection successful.");
    }

    [Fact]
    public async Task Init_PostgresFileReference_ProbesOwnedLoopbackAndPersistsReferenceOnly()
    {
        using var fixture = new InitWizardProcessFixture();

        using var result = await fixture.RunAsync("postgres-file");

        using var config = fixture.ReadConfig();
        config.RootElement.GetProperty("storage").GetString().ShouldBe("postgresql");
        config.RootElement.GetProperty("connectionString").GetString().ShouldBe($"file:{Path.Join(fixture.Private, "connection.secret")}");
        config.RootElement.GetRawText().ShouldNotContain("fixture-storage-file-sentinel");
        result.RootElement.GetProperty("probeConnections").GetInt32().ShouldBe(1);
        var output = result.RootElement.GetProperty("output").GetString();
        output.ShouldNotBeNull();
        output.ShouldContain("Connection successful.");
        output.ShouldNotContain("Could not connect");
        output.ShouldNotContain("fixture-storage-file-sentinel");
    }

    [Fact]
    public async Task Init_RefusedOwnedLoopbackProbe_WarnsWithoutClaimingConnectionSuccess()
    {
        using var fixture = new InitWizardProcessFixture();

        using var result = await fixture.RunAsync("postgres-file-refused");

        using var config = fixture.ReadConfig();
        config.RootElement.GetProperty("storage").GetString().ShouldBe("postgresql");
        config.RootElement.GetProperty("connectionString").GetString().ShouldBe($"file:{Path.Join(fixture.Private, "connection.secret")}");
        config.RootElement.GetRawText().ShouldNotContain("fixture-storage-file-sentinel");
        var output = result.RootElement.GetProperty("output").GetString();
        output.ShouldNotBeNull();
        output.ShouldContain("Could not connect — verify your connection string before starting.");
        output.ShouldNotContain("Connection successful.");
        output.ShouldNotContain("fixture-storage-file-sentinel");
    }
}
