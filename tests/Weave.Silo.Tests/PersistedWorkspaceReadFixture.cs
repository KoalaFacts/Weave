using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration.Overrides;
using Orleans.Storage;
using Weave.Silo.VirtualActors;
using Weave.Workspaces.Lifecycle;

namespace Weave.Silo.Tests;

internal sealed class PersistedWorkspaceReadFixture : IDisposable
{
    private const string WorkspaceStateName = "workspace";
    private string? _serviceId;
    public string Root { get; } = Directory.CreateTempSubdirectory("weave-workspace-read-").FullName;
    private string ConnectionString => $"Data Source={Path.Join(Root, "actors.db")};Pooling=False";

    public WebApplicationFactory<Program> CreateHost(SiloFactory parent) => parent.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Weave:ActorStorage:Provider", "sqlite");
        builder.UseSetting("ConnectionStrings:Sqlite", ConnectionString);
        builder.UseSetting("Weave:Invocations:DatabasePath", Path.Join(Root, "invocations.db"));
    });

    public static IWorkspaceActorGrain Actor(WebApplicationFactory<Program> host, string id) =>
        host.Services.GetRequiredService<IGrainFactory>().GetGrain<IWorkspaceActorGrain>(id);

    public async Task<string> ReadStateNameAsync(WebApplicationFactory<Program> host, string bootstrapId)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        // PersistentState("workspace", "Default") passes its state name verbatim
        // to Orleans 10.1 ADO.NET storage; the grain's string key is stored separately.
        _serviceId = host.Services.GetProviderClusterOptions("Default").Value.ServiceId;
        var grainId = Actor(host, bootstrapId).GetGrainId();
        grainId.Key.ToString().ShouldBe(bootstrapId);
        command.CommandText = """
            SELECT GrainTypeString FROM OrleansStorage
            WHERE GrainTypeString = $type AND GrainIdExtensionString = $id
                AND ServiceId = $service AND GrainIdN0 = 0 AND GrainIdN1 = 0
            """;
        command.Parameters.AddWithValue("$type", WorkspaceStateName);
        command.Parameters.AddWithValue("$id", grainId.Key.ToString());
        command.Parameters.AddWithValue("$service", _serviceId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        var name = reader.GetString(0);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeFalse("Exactly one workspace state row must match the bootstrap identity and service.");
        name.ShouldBe(WorkspaceStateName);
        return name;
    }

    public static async Task SeedAsync(WebApplicationFactory<Program> host, string stateName, WorkspaceState value)
    {
        var storage = host.Services.GetRequiredKeyedService<IGrainStorage>("Default");
        var grainId = Actor(host, value.WorkspaceId.ToString()).GetGrainId();
        var record = new GrainState<WorkspaceState>(new WorkspaceState());
        await storage.ReadStateAsync(stateName, grainId, record);
        record.State = value;
        await storage.WriteStateAsync(stateName, grainId, record);
        var readBack = new GrainState<WorkspaceState>(new WorkspaceState());
        await storage.ReadStateAsync(stateName, grainId, readBack);
        readBack.RecordExists.ShouldBeTrue();
        readBack.State.WorkspaceId.ShouldBe(value.WorkspaceId);
        readBack.State.Status.ShouldBe(value.Status);
    }

    public async Task<byte[]> ReadPayloadAsync(string id)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT PayloadBinary FROM OrleansStorage
            WHERE GrainTypeString = $type AND GrainIdExtensionString = $id
                AND ServiceId = $service AND GrainIdN0 = 0 AND GrainIdN1 = 0
            """;
        command.Parameters.AddWithValue("$type", WorkspaceStateName);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$service", _serviceId.ShouldNotBeNull());
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeTrue($"Workspace payload missing for {id}.");
        var payload = reader.GetFieldValue<byte[]>(0);
        (await reader.ReadAsync(TestContext.Current.CancellationToken)).ShouldBeFalse("Workspace identity must select exactly one payload.");
        return payload;
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
