using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orleans.Serialization;
using Orleans.TestingHost;
using Weave.Shared.Events;
using Weave.Shared.Lifecycle;

namespace Weave.Agents.Tests.TestCluster;

/// <summary>
/// Shared xUnit fixture that boots an in-memory Orleans <see cref="TestCluster"/>
/// once per test class. Use via <c>[Collection(nameof(WeaveClusterCollection))]</c>
/// or <c>IClassFixture&lt;WeaveTestCluster&gt;</c>.
///
/// The cluster mirrors the production Silo's actor configuration closely enough
/// to exercise: <c>[PersistentState(...,"Default")]</c> injection,
/// the Orleans timer API, actor-to-actor calls via <see cref="IActorFactory"/>,
/// and real actor activation lifecycle (<c>OnActivateAsync</c>).
///
/// It does <em>not</em> boot the HTTP pipeline — that's what <c>SiloFactory</c>
/// is for. Use this fixture for actor-internals tests; use <c>SiloFactory</c>
/// for endpoint integration tests.
/// </summary>
public sealed class WeaveTestCluster : IAsyncLifetime
{
    public Orleans.TestingHost.TestCluster Cluster { get; private set; } = null!;

    public IActorFactory ActorFactory => Cluster.GrainFactory;

    /// <summary>
    /// FakeTimeProvider shared across the cluster. Tests that need to advance
    /// virtual time call <c>cluster.Time.Advance(TimeSpan.FromMinutes(5))</c>
    /// — actors that inject <see cref="TimeProvider"/> see the updated time
    /// without any wall-clock waits.
    /// </summary>
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 4, 19, 12, 0, 0, TimeSpan.Zero));

    public ValueTask InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        // Silo configurators must be public and have a parameterless constructor
        // for Orleans.TestingHost to serialize them across the silo boundary.
        // Share the FakeTimeProvider via a static field on SiloConfigurator.
        SiloConfigurator.SharedTime = Time;
        Cluster = builder.Build();
        Cluster.Deploy();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Cluster is not null)
            await Cluster.StopAllSilosAsync();
    }

    /// <summary>
    /// Registers the minimum set of services required by <c>Weave.Agents</c>
    /// actors to activate. The production Silo registers these at the application
    /// root; here we register them per-silo inside the cluster.
    ///
    /// Actors that depend on concrete production services not registered here
    /// will fail activation with a clear DI resolution error — extend this
    /// list when a new actor test needs a collaborator.
    /// </summary>
    public sealed class SiloConfigurator : ISiloConfigurator
    {
        // Static handoff: Orleans.TestingHost reinstantiates the configurator
        // in the silo host, so instance state on the fixture can't flow through.
        // Setting this once in InitializeAsync before Deploy() is safe — each
        // WeaveTestCluster owns the silo host for its lifetime.
        internal static FakeTimeProvider? SharedTime;

        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorageAsDefault();

            // Scan the Silo's serialization assembly so branded-ID converters
            // (AgentTaskIdSurrogate, etc.) register. Without this the Orleans
            // serializer config validator throws CodecNotFoundException at startup
            // for every actor interface that returns or accepts a branded ID.
            siloBuilder.Services.AddSerializer(s =>
                s.AddAssembly(typeof(Weave.Silo.Serialization.SerializationMarker).Assembly));

            siloBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<ILifecycleManager, LifecycleManager>();
                services.AddSingleton<IEventBus, InProcessEventBus>();
                // FakeTimeProvider-as-TimeProvider so actor code under test
                // sees virtual time that tests can advance deterministically.
                services.AddSingleton<TimeProvider>(SharedTime ?? new FakeTimeProvider());
            });
        }
    }
}

// xUnit requires the CollectionDefinition class to expose ICollectionFixture<T>;
// the "Collection" suffix is the xUnit convention, not a naming smell — suppress CA1711.
[CollectionDefinition(nameof(WeaveClusterCollection))]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "xUnit collection-fixture convention")]
public sealed class WeaveClusterCollection : ICollectionFixture<WeaveTestCluster>;
