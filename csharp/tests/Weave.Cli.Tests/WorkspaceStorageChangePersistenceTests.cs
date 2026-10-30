using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tests;

public sealed class WorkspaceStorageChangePersistenceTests
{
    [Fact]
    public async Task ExecuteAsync_Memory_RemovesStorageOverrideAndPreservesWorkspace()
    {
        using var directory = new LocalTestDirectory();
        var path = Path.Join(directory.Root, "workspace.json");
        var original = Manifest();
        await WorkspaceManifestFile.WriteAsync(path, original, TestContext.Current.CancellationToken);

        var result = await Command(path).ExecuteAsync(
            new WorkspaceStorageChangeOptions("storage-test", "memory", null, null, null, null),
            TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        var updated = await WorkspaceManifestFile.ReadAsync(path, TestContext.Current.CancellationToken);
        updated.Workspace.Storage.ShouldBeNull();
        AssertNonStorageContentPreserved(original, updated);
    }

    [Fact]
    public async Task ExecuteAsync_NewSqliteFile_PersistsConnectionWithoutCreatingDatabase()
    {
        using var directory = new LocalTestDirectory();
        var path = Path.Join(directory.Root, "workspace.json");
        var databasePath = Path.Join(directory.Root, "new-store.db");
        var original = Manifest() with { Workspace = new WorkspaceConfig { Isolation = IsolationLevel.Full } };
        await WorkspaceManifestFile.WriteAsync(path, original, TestContext.Current.CancellationToken);
        var connection = "Data Source=" + databasePath;

        var result = await Command(path).ExecuteAsync(
            new WorkspaceStorageChangeOptions("storage-test", "sqlite", connection, "unused-schema", "selected-db", "database"),
            TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        var updated = await WorkspaceManifestFile.ReadAsync(path, TestContext.Current.CancellationToken);
        var storage = updated.Workspace.Storage.ShouldNotBeNull();
        storage.Backend.ShouldBe("sqlite");
        storage.ConnectionString.ShouldBe(connection);
        storage.Database.ShouldBe("selected-db");
        storage.Isolation.ShouldBe(StorageIsolation.Database);
        storage.Schema.ShouldBeNull();
        File.Exists(databasePath).ShouldBeFalse();
        AssertNonStorageContentPreserved(original, updated);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownBackend_PreservesOriginalFileBytes()
    {
        using var directory = new LocalTestDirectory();
        var path = Path.Join(directory.Root, "workspace.json");
        await WorkspaceManifestFile.WriteAsync(path, Manifest(), TestContext.Current.CancellationToken);
        var originalBytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        var result = await Command(path).ExecuteAsync(
            new WorkspaceStorageChangeOptions("storage-test", "unsupported-store", null, null, null, null),
            TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)).ShouldBe(originalBytes);
    }

    [Fact]
    public async Task ExecuteAsync_NoManifest_ReturnsFailure()
    {
        var result = await Command(null).ExecuteAsync(
            new WorkspaceStorageChangeOptions("missing", "memory", null, null, null, null),
            TestContext.Current.CancellationToken);

        result.ShouldBe(1);
    }

    private static WorkspaceStorageChangeCliCommand Command(string? path)
    {
        var resolver = new FixedManifestResolver(path);
        return new WorkspaceStorageChangeCliCommand(resolver, new WorkspacePrompt(new RecordingWorkspaceRegistry(), resolver));
    }

    private static WorkspaceManifest Manifest() => new()
    {
        Version = "1.0",
        Name = "storage-test",
        Workspace = new WorkspaceConfig
        {
            Isolation = IsolationLevel.Full,
            Network = new NetworkConfig { Name = "preserved-network" },
            Secrets = new SecretsConfig { Provider = "env" },
            Storage = new StorageConfig
            {
                Backend = "postgresql",
                ConnectionString = "Host=unused.invalid;Database=old",
                Database = "old",
                Schema = "old-schema",
                Isolation = StorageIsolation.Schema
            }
        },
        Agents = new Dictionary<string, AgentDefinition>
        {
            ["worker"] = new() { Model = "preserved-model", Tools = ["files"], Capabilities = ["tool:files:invoke:read"] }
        },
        Tools = new Dictionary<string, ToolDefinition> { ["files"] = new() { Type = "filesystem" } },
        Targets = new Dictionary<string, TargetDefinition> { ["local"] = new() { Runtime = "podman" } },
        Channels = new Dictionary<string, ChannelDefinition> { ["updates"] = new() { Type = "webhook", TargetAgent = "worker" } }
    };

    private static void AssertNonStorageContentPreserved(WorkspaceManifest original, WorkspaceManifest updated)
    {
        var parser = new ManifestParser();
        var withoutStorageChange = updated with { Workspace = updated.Workspace with { Storage = original.Workspace.Storage } };
        parser.Serialize(withoutStorageChange).ShouldBe(parser.Serialize(original));
    }
}
