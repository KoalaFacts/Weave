namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class StorageCommandPersistenceProcessTests
{
    [Theory]
    [InlineData("unsupported-fixture", false, 1, "Unknown backend 'unsupported-fixture'")]
    [InlineData("MEMORY", false, 0, "Already using 'MEMORY'.")]
    [InlineData("sqlite", true, 1, "Server is still running. Stop it first:")]
    public async Task StorageChange_RejectedOrUnnecessaryChange_PreservesConfigBytes(
        string backend, bool running, int expectedExit, string diagnostic)
    {
        using var cli = new IsolatedCliProcess();
        await cli.AssertPrivateHomeAsync();
        await using var health = new StorageHealthEndpoint(running);
        await StorageChangeTerminalProcessTests.SeedAsync(cli.ConfigPath, health.Port, "memory", null);
        var before = await File.ReadAllBytesAsync(cli.ConfigPath, TestContext.Current.CancellationToken);

        var result = await cli.RunAsync("storage", "change", backend);

        result.ExitCode.ShouldBe(expectedExit, result.StandardError);
        result.VisibleOutput.ShouldContain(diagnostic);
        result.VisibleOutput.ShouldNotContain("Storage changed:");
        (await File.ReadAllBytesAsync(cli.ConfigPath, TestContext.Current.CancellationToken)).ShouldBe(before);
        await health.AssertHealthRequestedAsync();
    }

    [Fact]
    public async Task StorageChange_MigrateFlag_PersistsMemoryAndExplainsImportWithoutTouchingData()
    {
        using var cli = new IsolatedCliProcess();
        await cli.AssertPrivateHomeAsync();
        await using var health = new StorageHealthEndpoint(running: false);
        await StorageChangeTerminalProcessTests.SeedAsync(cli.ConfigPath, health.Port, "sqlite", "Data Source=owned-sentinel.db");
        var data = Path.Join(cli.Root, "owned-sentinel.db");
        await File.WriteAllTextAsync(data, "unchanged fixture database", TestContext.Current.CancellationToken);

        var result = await cli.RunAsync("storage", "change", "memory", "--migrate");

        result.ExitCode.ShouldBe(0, result.StandardError);
        result.VisibleOutput.ShouldContain("Storage changed: sqlite → memory");
        result.VisibleOutput.ShouldContain("Use 'weave data import' to restore your exported data on the new backend.");
        result.VisibleOutput.ShouldNotContain("To migrate existing data:");
        using var config = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(cli.ConfigPath, TestContext.Current.CancellationToken));
        config.RootElement.GetProperty("storage").GetString().ShouldBe("memory");
        config.RootElement.TryGetProperty("connectionString", out _).ShouldBeFalse();
        (await File.ReadAllTextAsync(data, TestContext.Current.CancellationToken)).ShouldBe("unchanged fixture database");
        await health.AssertHealthRequestedAsync();
    }

    [Theory]
    [InlineData("memory", null, "(none — in-memory)")]
    [InlineData("sqlite", null, "(not configured)")]
    [InlineData("postgresql", "env:FIXTURE_CONNECTION_NOT_READ", "(from environment variable)")]
    [InlineData("postgresql", "file:/fixture/missing-secret", "(from secret file)")]
    [InlineData("postgresql", "vault:fixture/database#connection", "(from HashiCorp Vault)")]
    [InlineData("postgresql", "Host=owned-fixture;Password=display-secret-sentinel", "inline — not recommended")]
    public async Task StorageShow_DisplaysReferenceOrMaskedInlineValueWithoutChangingConfig(
        string backend, string? connection, string expected)
    {
        using var cli = new IsolatedCliProcess();
        await cli.AssertPrivateHomeAsync();
        await StorageChangeTerminalProcessTests.SeedAsync(cli.ConfigPath, 9401, backend, connection);
        var before = await File.ReadAllBytesAsync(cli.ConfigPath, TestContext.Current.CancellationToken);

        var result = await cli.RunAsync("storage", "show");

        result.ExitCode.ShouldBe(0, result.StandardError);
        result.VisibleOutput.ShouldContain("Storage Configuration");
        result.VisibleOutput.ShouldContain(backend);
        var visibleText = string.Join(' ', result.VisibleOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        visibleText.ShouldContain(expected);
        result.VisibleOutput.ShouldNotContain("display-secret-sentinel");
        if (connection is not null && !connection.StartsWith("Host=", StringComparison.Ordinal))
            result.VisibleOutput.ShouldContain(connection);
        if (connection?.StartsWith("Host=", StringComparison.Ordinal) == true)
        {
            result.VisibleOutput.ShouldContain("Host=owned-fixture");
            result.VisibleOutput.ShouldContain("Password=***");
        }
        if (backend == "sqlite")
        {
            result.VisibleOutput.ShouldContain(Path.Join(cli.WeaveHome, "weave.db"));
            File.Exists(Path.Join(cli.WeaveHome, "weave.db")).ShouldBeFalse();
        }
        (await File.ReadAllBytesAsync(cli.ConfigPath, TestContext.Current.CancellationToken)).ShouldBe(before);
    }
}
