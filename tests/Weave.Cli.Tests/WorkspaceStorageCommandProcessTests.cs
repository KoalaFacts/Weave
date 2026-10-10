using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class WorkspaceStorageCommandProcessTests
{
    [Fact]
    public async Task RegisteredCommands_ExplicitOptionsPersistSchemaAndShowMasksPassword()
    {
        using var cli = new IsolatedCliProcess();
        await cli.AssertPrivateHomeAsync();
        var manifestPath = await SeedAsync(cli);
        var registryPath = Path.Join(cli.WeaveHome, "workspaces.json");
        var registry = await File.ReadAllTextAsync(registryPath, TestContext.Current.CancellationToken);
        using var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        var connection = $"Server=127.0.0.1,{port};Database=owned;Password=synthetic-display-secret";

        var changed = await cli.RunAsync("workspace", "storage", "change", "registered-team", "sqlserver",
            "--connection", connection, "--database", "owned", "--isolation", "schema", "--schema", "team_schema");
        var shown = await cli.RunAsync("workspace", "storage", "show", "registered-team");

        changed.ExitCode.ShouldBe(0, changed.StandardError);
        changed.VisibleOutput.ShouldContain("storage set to sqlserver.");
        var storage = (await WorkspaceManifestFile.ReadAsync(manifestPath, TestContext.Current.CancellationToken)).Workspace.Storage.ShouldNotBeNull();
        storage.Backend.ShouldBe("sqlserver");
        storage.Isolation.ShouldBe(StorageIsolation.Schema);
        storage.Schema.ShouldBe("team_schema");
        storage.Database.ShouldBe("owned");
        storage.ConnectionString.ShouldBe(connection);
        shown.ExitCode.ShouldBe(0, shown.StandardError);
        shown.VisibleOutput.ShouldContain("sqlserver");
        shown.VisibleOutput.ShouldContain("team_schema");
        shown.VisibleOutput.ShouldContain("owned");
        shown.VisibleOutput.ShouldContain("Password=***");
        shown.VisibleOutput.ShouldNotContain("synthetic-display-secret");
        (await File.ReadAllTextAsync(registryPath, TestContext.Current.CancellationToken)).ShouldBe(registry);
        File.Exists(cli.ConfigPath).ShouldBeFalse();
    }

    [Fact]
    public async Task RegisteredCommands_DefaultAndMissingWorkspaceAndUnknownBackendPreserveManifest()
    {
        using var cli = new IsolatedCliProcess();
        await cli.AssertPrivateHomeAsync();
        var manifestPath = await SeedAsync(cli);
        var original = await File.ReadAllBytesAsync(manifestPath, TestContext.Current.CancellationToken);

        var shown = await cli.RunAsync("workspace", "storage", "show", "registered-team");
        var missing = await cli.RunAsync("workspace", "storage", "show", "unregistered-team");
        var rejected = await cli.RunAsync("workspace", "storage", "change", "registered-team", "unknown-fixture");

        shown.ExitCode.ShouldBe(0, shown.StandardError);
        shown.VisibleOutput.ShouldContain("(uses global default)");
        shown.VisibleOutput.ShouldContain("weave storage show");
        missing.ExitCode.ShouldBe(1);
        missing.VisibleOutput.ShouldContain("No workspace.json found for 'unregistered-team'.");
        rejected.ExitCode.ShouldBe(1);
        rejected.VisibleOutput.ShouldContain("Unknown backend 'unknown-fixture'");
        (await File.ReadAllBytesAsync(manifestPath, TestContext.Current.CancellationToken)).ShouldBe(original);
        File.Exists(cli.ConfigPath).ShouldBeFalse();
    }

    [Fact]
    public async Task RegisteredCommands_SafeSqliteFileReference_IsStoredAndDisplayedWithoutDisclosingSecret()
    {
        using var cli = new IsolatedCliProcess();
        await cli.AssertPrivateHomeAsync();
        var manifestPath = await SeedAsync(cli);
        var secretPath = Path.Join(cli.Root, "reference-only.secret");
        await File.WriteAllTextAsync(secretPath, "must-not-be-read-sentinel", TestContext.Current.CancellationToken);
        var reference = "file:" + secretPath;

        var changed = await cli.RunAsync("workspace", "storage", "change", "registered-team", "sqlite", "--connection", reference);
        var shown = await cli.RunAsync("workspace", "storage", "show", "registered-team");

        changed.ExitCode.ShouldBe(0, changed.StandardError);
        var storage = (await WorkspaceManifestFile.ReadAsync(manifestPath, TestContext.Current.CancellationToken)).Workspace.Storage.ShouldNotBeNull();
        storage.ConnectionString.ShouldBe(reference);
        shown.ExitCode.ShouldBe(0, shown.StandardError);
        string.Join("", shown.VisibleOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ShouldContain(reference);
        shown.VisibleOutput.ShouldNotContain("must-not-be-read-sentinel");
        File.ReadAllText(secretPath).ShouldBe("must-not-be-read-sentinel");
        File.Exists(cli.ConfigPath).ShouldBeFalse();
    }

    private static async Task<string> SeedAsync(IsolatedCliProcess cli)
    {
        var folder = Directory.CreateDirectory(Path.Join(cli.Root, "registered workspace")).FullName;
        var path = Path.Join(folder, "workspace.json");
        await File.WriteAllTextAsync(path, """
            {"name":"registered-team","version":"1.0","workspace":{"isolation":"full"},"agents":{},"tools":{}}
            """, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(cli.WeaveHome);
        var registry = JsonSerializer.Serialize(new Dictionary<string, string> { ["registered-team"] = folder });
        await File.WriteAllTextAsync(Path.Join(cli.WeaveHome, "workspaces.json"), registry, TestContext.Current.CancellationToken);
        return path;
    }
}
