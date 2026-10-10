using System.Text.Json;

namespace Weave.Cli.Tests;

[Collection("CLI init PTY")]
[Trait("Category", "Integration")]
public sealed class StorageChangeTerminalProcessTests
{
    [Fact]
    public async Task StorageChange_MenuSqlite_PersistsPrivateDatabasePathWithoutCreatingDatabase()
    {
        using var fixture = new InitWizardProcessFixture();
        await using var health = new StorageHealthEndpoint(running: false);
        await SeedAsync(fixture.ConfigPath, health.Port, "memory", null);

        using var result = await fixture.RunAsync("storage-sqlite");

        await health.AssertHealthRequestedAsync();
        using var config = fixture.ReadConfig();
        config.RootElement.GetProperty("storage").GetString().ShouldBe("sqlite");
        config.RootElement.GetProperty("connectionString").GetString()
            .ShouldBe($"Data Source={Path.Join(fixture.Private, "weave.db")}");
        config.RootElement.GetProperty("defaultPort").GetInt32().ShouldBe(health.Port);
        config.RootElement.GetProperty("siloPath").GetString().ShouldBe("/fixture/runtime");
        File.Exists(Path.Join(fixture.Private, "weave.db")).ShouldBeFalse();
        var output = result.RootElement.GetProperty("output").GetString().ShouldNotBeNull();
        output.ShouldContain("New backend:");
        output.ShouldContain("Storage changed: memory → sqlite");
    }

    [Fact]
    public async Task StorageChange_MenuMemory_ClearsConnectionReferenceAndKeepsExistingDatabase()
    {
        using var fixture = new InitWizardProcessFixture();
        await using var health = new StorageHealthEndpoint(running: false);
        var database = Path.Join(fixture.Private, "weave.db");
        await File.WriteAllTextAsync(database, "owned existing database sentinel", TestContext.Current.CancellationToken);
        await SeedAsync(fixture.ConfigPath, health.Port, "sqlite", $"Data Source={database}");

        using var result = await fixture.RunAsync("storage-memory");

        await health.AssertHealthRequestedAsync();
        using var config = fixture.ReadConfig();
        config.RootElement.GetProperty("storage").GetString().ShouldBe("memory");
        config.RootElement.TryGetProperty("connectionString", out _).ShouldBeFalse();
        config.RootElement.GetProperty("defaultPort").GetInt32().ShouldBe(health.Port);
        (await File.ReadAllTextAsync(database, TestContext.Current.CancellationToken))
            .ShouldBe("owned existing database sentinel");
        var output = result.RootElement.GetProperty("output").GetString().ShouldNotBeNull();
        output.ShouldContain("Storage changed: sqlite → memory");
        output.ShouldContain("weave data export");
        output.ShouldContain("weave data import");
    }

    internal static async Task SeedAsync(string path, int port, string storage, string? connection)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path).ShouldNotBeNull());
        var json = JsonSerializer.Serialize(new
        {
            version = "1.0",
            siloPath = "/fixture/runtime",
            defaultPort = port,
            storage,
            connectionString = connection,
            authMode = "none",
            requireHttps = false,
            preserveMarker = "byte preservation sentinel"
        });
        await File.WriteAllTextAsync(path, json + "\n", TestContext.Current.CancellationToken);
    }
}
