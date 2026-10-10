using System.Net;
using System.Net.Sockets;
using Weave.Cli.Commands;
using Weave.Workspaces.Manifest;

namespace Weave.Cli.Tests;

[Trait("Category", "Integration")]
public sealed class WorkspaceStorageSchemaProcessTests
{
    [Fact]
    public Task Change_SchemaIsolation_UsesOwnedTcpProbeAndPersistsDefaultSchemaAfterRename() =>
        SiloLauncherProcessHarness.RunAsync(typeof(WorkspaceStorageSchemaProcessTests), async root =>
        {
            var fixture = new WorkspaceStorageDiskFixture(root);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var connection = $"Host=127.0.0.1;Port={port};Database=shared;Password=synthetic-unused-password";
            using var console = new MarketplacePromptConsole();
            console.Key(ConsoleKey.DownArrow);
            console.Key(ConsoleKey.DownArrow);
            console.Line("");
            console.Line("renamed_shared");
            console.Line("");

            (await fixture.Change.ExecuteAsync(new WorkspaceStorageChangeOptions(
                WorkspaceStorageDiskFixture.Name, "postgresql", connection, null, "shared", "SCHEMA"),
                TestContext.Current.CancellationToken)).ShouldBe(0);

            using var accepted = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            IPAddress.IsLoopback(((IPEndPoint)accepted.Client.RemoteEndPoint!).Address).ShouldBeTrue();
            var bytes = new byte[1];
            (await accepted.GetStream().ReadAsync(bytes, TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken)).ShouldBe(0);
            var storage = (await fixture.ReadAsync()).Workspace.Storage.ShouldNotBeNull();
            storage.Backend.ShouldBe("postgresql");
            storage.Isolation.ShouldBe(StorageIsolation.Schema);
            storage.Schema.ShouldBe(WorkspaceStorageDiskFixture.Name);
            storage.Database.ShouldBe("renamed_shared");
            storage.ConnectionString.ShouldBe(connection.Replace("Database=shared", "Database=renamed_shared", StringComparison.Ordinal));
            console.RemainingKeys.ShouldBe(0);
            console.Text.ShouldContain("Schema name:");
            console.Text.ShouldContain("storage set to postgresql.");
            console.Text.ShouldNotContain("synthetic-unused-password");
            fixture.AssertOtherStatePreserved();
        });
}
