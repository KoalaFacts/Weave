using System.Text.Json;

namespace Weave.Cli.Tests;

[Collection("CLI init PTY")]
[Trait("Category", "Integration")]
public sealed class StorageConnectionTerminalTests
{
    [Theory]
    [InlineData("next-storage-postgresql", "postgresql", true)]
    [InlineData("next-storage-sqlserver", "sqlserver", true)]
    [InlineData("next-storage-redis", "redis", true)]
    [InlineData("next-storage-postgresql-refused", "postgresql", false)]
    public async Task StorageChange_EnteredConnection_PersistsExactValueAndReportsActualTcpReachability(
        string scenario, string backend, bool reachable)
    {
        using var fixture = new InitWizardProcessFixture();
        await using var health = new StorageHealthEndpoint(running: false);
        await StorageChangeTerminalProcessTests.SeedAsync(fixture.ConfigPath, health.Port, "memory", null);

        using var result = await fixture.RunAsync(scenario);

        await health.AssertHealthRequestedAsync();
        using var config = fixture.ReadConfig();
        using var input = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Join(fixture.Private, "fixture-input.json"), TestContext.Current.CancellationToken));
        config.RootElement.GetProperty("storage").GetString().ShouldBe(backend);
        config.RootElement.GetProperty("connectionString").GetString()
            .ShouldBe(input.RootElement.GetProperty("connection").GetString().ShouldNotBeNull());
        config.RootElement.GetProperty("defaultPort").GetInt32().ShouldBe(health.Port);
        config.RootElement.GetProperty("siloPath").GetString().ShouldBe("/fixture/runtime");
        config.RootElement.GetProperty("authMode").GetString().ShouldBe("none");
        result.RootElement.GetProperty("probeConnections").GetInt32().ShouldBe(reachable ? 1 : 0);
        var output = result.RootElement.GetProperty("output").GetString().ShouldNotBeNull();
        output.ShouldContain("Storage changed: memory → " + backend);
        output.ShouldContain(reachable ? "Connection successful." : "Could not connect — saving anyway.");
        output.ShouldNotContain(reachable ? "Could not connect" : "Connection successful.");
        if (backend is "postgresql" or "sqlserver")
            output.ShouldContain("Run the Orleans SQL scripts before starting:");
        else
            output.ShouldNotContain("Run the Orleans SQL scripts before starting:");
        File.Exists(Path.Join(fixture.Private, "weave.db")).ShouldBeFalse();
    }
}
